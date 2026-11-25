using System.Net;
using System.Net.Sockets;

namespace StormConnect.Protocol;

public static class WakeOnLan
{
    public static byte[] ParseMacAddress(string macAddress)
    {
        var hex = new string(macAddress.Where(c => Uri.IsHexDigit(c)).ToArray());
        if (hex.Length != 12)
            throw new ArgumentException($"Invalid MAC address: {macAddress}");

        var mac = new byte[6];
        for (int i = 0; i < 6; i++)
            mac[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return mac;
    }

    public static byte[] BuildMagicPacket(string macAddress)
    {
        var mac = ParseMacAddress(macAddress);
        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 1; i <= 16; i++) Buffer.BlockCopy(mac, 0, packet, i * 6, 6);
        return packet;
    }

    public static async Task SendAsync(string macAddress, string? broadcastIp = null, CancellationToken ct = default)
    {
        var packet = BuildMagicPacket(macAddress);
        using var udp = new UdpClient { EnableBroadcast = true };
        var target = string.IsNullOrWhiteSpace(broadcastIp) ? IPAddress.Broadcast : IPAddress.Parse(broadcastIp);
        await udp.SendAsync(packet, packet.Length, new IPEndPoint(target, 9)).WaitAsync(ct);
        await udp.SendAsync(packet, packet.Length, new IPEndPoint(target, 7)).WaitAsync(ct);
    }
}
