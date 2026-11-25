using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using StormConnect.Protocol;

namespace StormConnect.Desktop.Networking;

public sealed record DiscoveredDevice(
    string StormId,
    string HostName,
    string OsName,
    int MonitorCount,
    int DirectPort,
    string LocalIp,
    DateTime LastSeenUtc
);

/// <summary>
/// UDP broadcast LAN discovery service on port 7445 so Windows and Android peers
/// on the same subnet discover each other automatically without manual IP entry.
/// </summary>
public sealed class LanDiscoveryService : IDisposable
{
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _devices = new();
    private CancellationTokenSource? _cts;
    private Func<LanBeaconPacket>? _beaconFactory;
    private string _selfId = string.Empty;

    public event Action<IReadOnlyList<DiscoveredDevice>>? DevicesUpdated;

    public IReadOnlyList<DiscoveredDevice> CurrentDevices =>
        _devices.Values
            .Where(d => (DateTime.UtcNow - d.LastSeenUtc).TotalSeconds < 15)
            .OrderBy(d => d.HostName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void Start(string selfStormId, Func<LanBeaconPacket> beaconFactory)
    {
        if (_cts is not null) return;
        _selfId = DeviceIdentity.NormalizeId(selfStormId);
        _beaconFactory = beaconFactory;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        Task.Run(() => ListenLoopAsync(ct), ct);
        Task.Run(() => BroadcastLoopAsync(ct), ct);
    }

    public static string GetPrimaryLocalIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                        return addr.Address.ToString();
                }
            }

            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    return ip.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }

    public static (string? LanIpv4, string? VpnIpv4, string? GlobalIpv6) GetAllReachableAddresses()
    {
        string? lan = null;
        string? vpn = null;
        string? v6 = null;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                string desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
                bool looksLikeVpn = desc.Contains("tailscale") || desc.Contains("wireguard") || desc.Contains("radmin") ||
                                    desc.Contains("zerotier") || desc.Contains("vpn") || desc.Contains("tun") || desc.Contains("tap");

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (IPAddress.IsLoopback(addr.Address)) continue;
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ipStr = addr.Address.ToString();
                        if (looksLikeVpn || ipStr.StartsWith("100.", StringComparison.Ordinal) || ipStr.StartsWith("26.", StringComparison.Ordinal))
                            vpn ??= ipStr;
                        else
                            lan ??= ipStr;
                    }
                    else if (addr.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                             !addr.Address.IsIPv6LinkLocal &&
                             !addr.Address.IsIPv6SiteLocal &&
                             !addr.Address.IsIPv6Multicast)
                    {
                        var s = addr.Address.ToString();
                        if (!s.StartsWith("fe80", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("fc", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("fd", StringComparison.OrdinalIgnoreCase))
                            v6 ??= s;
                    }
                }
            }
        }
        catch { }
        return (lan ?? GetPrimaryLocalIp(), vpn, v6);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, ProtocolConstants.DefaultLanDiscoveryPort));

            while (!ct.IsCancellationRequested)
            {
                var res = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var pkt = FrameCodec.DeserializeJson<LanBeaconPacket>(res.Buffer);
                if (pkt is null) continue;
                var normId = DeviceIdentity.NormalizeId(pkt.StormId);
                if (normId == _selfId || normId.Length != 9) continue;

                var ip = string.IsNullOrWhiteSpace(pkt.LocalIp) ? res.RemoteEndPoint.Address.ToString() : pkt.LocalIp;
                _devices[normId] = new DiscoveredDevice(
                    normId,
                    pkt.HostName,
                    pkt.OsName,
                    pkt.MonitorCount,
                    pkt.DirectPort,
                    ip,
                    DateTime.UtcNow);

                DevicesUpdated?.Invoke(CurrentDevices);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            udp?.Dispose();
        }
    }

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_beaconFactory is not null)
                {
                    var beacon = _beaconFactory();
                    var bytes = FrameCodec.SerializeJson(beacon);
                    await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, ProtocolConstants.DefaultLanDiscoveryPort)).ConfigureAwait(false);
                }

                // Prune stale devices
                bool removed = false;
                foreach (var kv in _devices)
                {
                    if ((DateTime.UtcNow - kv.Value.LastSeenUtc).TotalSeconds > 15)
                        removed |= _devices.TryRemove(kv.Key, out _);
                }
                if (removed) DevicesUpdated?.Invoke(CurrentDevices);
            }
            catch { }

            try { await Task.Delay(2500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
