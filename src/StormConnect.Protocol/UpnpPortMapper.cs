using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace StormConnect.Protocol;

public sealed record UpnpMappingResult(bool Success, string? ExternalIp, int MappedExternalPort, string Method);

/// <summary>
/// Pure SSDP + UPnP IGD SOAP + NAT-PMP (RFC 6886) automatic port mapper
/// enabling direct inbound Internet connections through home/office routers without external servers.
/// </summary>
public static class UpnpPortMapper
{
    public static async Task<UpnpMappingResult> TryMapPortAsync(int internalPort, string localIp, string protocol = "TCP", CancellationToken ct = default)
    {
        // 1. Try UPnP IGD via SSDP multicast
        try
        {
            var location = await DiscoverIgdLocationAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(location))
            {
                var controlUrl = await FetchControlUrlAsync(location, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(controlUrl))
                {
                    bool mapped = await SendAddPortMappingAsync(controlUrl, internalPort, internalPort, localIp, protocol, ct).ConfigureAwait(false);
                    if (mapped)
                    {
                        var extIp = await GetExternalIpAsync(controlUrl, ct).ConfigureAwait(false);
                        return new UpnpMappingResult(true, extIp, internalPort, "UPnP IGD");
                    }
                }
            }
        }
        catch { }

        // 2. Fallback to NAT-PMP (RFC 6886) on default gateway UDP 5351
        try
        {
            var gw = GetDefaultGatewayIpv4();
            if (gw is not null)
            {
                var pmp = await TryNatPmpAsync(gw, internalPort, protocol == "UDP", ct).ConfigureAwait(false);
                if (pmp.Success) return pmp;
            }
        }
        catch { }

        return new UpnpMappingResult(false, null, 0, "None");
    }

    private static async Task<string?> DiscoverIgdLocationAsync(CancellationToken ct)
    {
        const string mSearch =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n\r\n";

        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = 2000;
        var bytes = Encoding.ASCII.GetBytes(mSearch);
        var ep = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        await udp.SendAsync(bytes, bytes.Length, ep).WaitAsync(ct).ConfigureAwait(false);

        using var rxCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        rxCts.CancelAfter(2000);
        var res = await udp.ReceiveAsync(rxCts.Token).ConfigureAwait(false);
        var txt = Encoding.UTF8.GetString(res.Buffer);
        var m = Regex.Match(txt, @"(?im)^LOCATION:\s*(.+)$");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static async Task<string?> FetchControlUrlAsync(string locationUrl, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var xml = await http.GetStringAsync(locationUrl, ct).ConfigureAwait(false);
        var m = Regex.Match(xml, @"<serviceType>urn:schemas-upnp-org:service:WAN(?:IP|PPP)Connection:1</serviceType>.*?<controlURL>(.*?)</controlURL>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var ctrl = m.Groups[1].Value.Trim();
        var baseUri = new Uri(locationUrl);
        return new Uri(baseUri, ctrl).ToString();
    }

    private static async Task<bool> SendAddPortMappingAsync(string controlUrl, int extPort, int intPort, string localIp, string proto, CancellationToken ct)
    {
        string body = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
  <s:Body>
    <u:AddPortMapping xmlns:u=""urn:schemas-upnp-org:service:WANIPConnection:1"">
      <NewRemoteHost></NewRemoteHost>
      <NewExternalPort>{extPort}</NewExternalPort>
      <NewProtocol>{proto}</NewProtocol>
      <NewInternalPort>{intPort}</NewInternalPort>
      <NewInternalClient>{localIp}</NewInternalClient>
      <NewEnabled>1</NewEnabled>
      <NewPortMappingDescription>STORM CONNECT</NewPortMappingDescription>
      <NewLeaseDuration>7200</NewLeaseDuration>
    </u:AddPortMapping>
  </s:Body>
</s:Envelope>";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl);
        req.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:WANIPConnection:1#AddPortMapping\"");
        req.Content = new StringContent(body, Encoding.UTF8, "text/xml");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }

    private static async Task<string?> GetExternalIpAsync(string controlUrl, CancellationToken ct)
    {
        const string body = @"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
  <s:Body><u:GetExternalIPAddress xmlns:u=""urn:schemas-upnp-org:service:WANIPConnection:1""/></s:Body>
</s:Envelope>";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl);
        req.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:WANIPConnection:1#GetExternalIPAddress\"");
        req.Content = new StringContent(body, Encoding.UTF8, "text/xml");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var m = Regex.Match(xml, @"<NewExternalIPAddress>(.*?)</NewExternalIPAddress>", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static async Task<UpnpMappingResult> TryNatPmpAsync(IPAddress gateway, int port, bool isUdp, CancellationToken ct)
    {
        using var udp = new UdpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(1500);

        // Request external address
        await udp.SendAsync(new byte[] { 0, 0 }, 2, new IPEndPoint(gateway, 5351)).WaitAsync(cts.Token).ConfigureAwait(false);
        var ipRes = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
        string? extIp = ipRes.Buffer.Length >= 12 ? new IPAddress(ipRes.Buffer.AsSpan(8, 4)).ToString() : null;

        // Request port mapping
        var req = new byte[12];
        req[0] = 0; // Version 0
        req[1] = isUdp ? (byte)1 : (byte)2;
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(4, 2), (ushort)port);
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(6, 2), (ushort)port);
        BinaryPrimitives.WriteUInt32BigEndian(req.AsSpan(8, 4), 7200);
        await udp.SendAsync(req, req.Length, new IPEndPoint(gateway, 5351)).WaitAsync(cts.Token).ConfigureAwait(false);

        var mapRes = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
        if (mapRes.Buffer.Length >= 16 && BinaryPrimitives.ReadUInt16BigEndian(mapRes.Buffer.AsSpan(2, 2)) == 0)
        {
            int mappedPort = BinaryPrimitives.ReadUInt16BigEndian(mapRes.Buffer.AsSpan(10, 2));
            return new UpnpMappingResult(true, extIp, mappedPort, "NAT-PMP");
        }
        return new UpnpMappingResult(false, null, 0, "None");
    }

    private static IPAddress? GetDefaultGatewayIpv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var gw in ni.GetIPProperties().GatewayAddresses)
            {
                if (gw.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(gw.Address))
                    return gw.Address;
            }
        }
        return null;
    }
}
