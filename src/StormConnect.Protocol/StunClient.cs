using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace StormConnect.Protocol;

/// <summary>
/// Pure RFC 5389 STUN Binding client using public STUN servers to discover
/// external IPv4 address and NAT-mapped UDP port with zero private server required.
/// </summary>
public static class StunClient
{
    private static readonly (string Host, int Port)[] PublicStunServers =
    [
        ("stun.l.google.com", 19302),
        ("stun1.l.google.com", 19302),
        ("stun.cloudflare.com", 3478),
        ("stun.stunprotocol.org", 3478)
    ];

    public static async Task<IPEndPoint?> DiscoverPublicEndpointAsync(int localUdpPort = 0, CancellationToken ct = default)
    {
        using var udp = new UdpClient(localUdpPort > 0 ? new IPEndPoint(IPAddress.Any, localUdpPort) : new IPEndPoint(IPAddress.Any, 0));
        udp.Client.ReceiveTimeout = 2000;

        foreach (var (host, port) in PublicStunServers)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                var v4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (v4 is null) continue;

                var txId = RandomNumberGenerator.GetBytes(12);
                var req = BuildBindingRequest(txId);
                await udp.SendAsync(req, req.Length, new IPEndPoint(v4, port)).WaitAsync(ct).ConfigureAwait(false);

                using var rxCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                rxCts.CancelAfter(1800);
                var resp = await udp.ReceiveAsync(rxCts.Token).ConfigureAwait(false);
                var parsed = ParseBindingResponse(resp.Buffer, txId);
                if (parsed is not null) return parsed;
            }
            catch { }
        }
        return null;
    }

    public static byte[] BuildBindingRequest(ReadOnlySpan<byte> transactionId12)
    {
        var buf = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), 0x0001); // Binding Request
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), 0);      // Length
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), 0x2112A442); // Magic Cookie
        transactionId12[..12].CopyTo(buf.AsSpan(8, 12));
        return buf;
    }

    public static IPEndPoint? ParseBindingResponse(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expectedTxId)
    {
        if (data.Length < 20) return null;
        ushort msgType = BinaryPrimitives.ReadUInt16BigEndian(data[..2]);
        if (msgType != 0x0101) return null; // Binding Success Response
        uint cookie = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        if (cookie != 0x2112A442) return null;
        if (!data.Slice(8, 12).SequenceEqual(expectedTxId[..12])) return null;

        int msgLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
        int offset = 20;
        int end = Math.Min(data.Length, 20 + msgLen);

        while (offset + 4 <= end)
        {
            ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            ushort attrLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
            offset += 4;
            if (offset + attrLen > end) break;

            var val = data.Slice(offset, attrLen);
            if (attrType == 0x0020 && attrLen >= 8 && val[1] == 0x01) // XOR-MAPPED-ADDRESS IPv4
            {
                ushort xPort = BinaryPrimitives.ReadUInt16BigEndian(val.Slice(2, 2));
                int port = xPort ^ 0x2112;
                Span<byte> ipBytes = stackalloc byte[4];
                ipBytes[0] = (byte)(val[4] ^ 0x21);
                ipBytes[1] = (byte)(val[5] ^ 0x12);
                ipBytes[2] = (byte)(val[6] ^ 0xA4);
                ipBytes[3] = (byte)(val[7] ^ 0x42);
                return new IPEndPoint(new IPAddress(ipBytes), port);
            }
            if (attrType == 0x0001 && attrLen >= 8 && val[1] == 0x01) // MAPPED-ADDRESS IPv4
            {
                int port = BinaryPrimitives.ReadUInt16BigEndian(val.Slice(2, 2));
                return new IPEndPoint(new IPAddress(val.Slice(4, 4)), port);
            }

            offset += (attrLen + 3) & ~3; // 4-byte attribute padding
        }
        return null;
    }
}
