using System.Text;
using System.Text.Json;

namespace StormConnect.Protocol;

public sealed record InviteCodePayload(
    string StormId,
    string HostName,
    int DirectPort,
    string? LanIp,
    string? VpnIp,
    string? PublicIpv4,
    int MappedTcpPort,
    string? StunUdpEndpoint,
    string? GlobalIpv6
);

/// <summary>
/// Encodes and decodes compact shareable serverless Invite Codes (SC1-...)
/// containing all direct LAN, VPN, IPv6, UPnP IPv4, and STUN endpoints.
/// </summary>
public static class InviteCode
{
    public const string Prefix = "SC1-";

    public static string Encode(InviteCodePayload payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var b64 = Convert.ToBase64String(json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return Prefix + b64;
    }

    public static bool TryDecode(string? input, out InviteCodePayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var trimmed = input.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            var b64 = trimmed[Prefix.Length..]
                .Replace('-', '+')
                .Replace('_', '/');
            int mod = b64.Length % 4;
            if (mod == 2) b64 += "==";
            else if (mod == 3) b64 += "=";
            else if (mod == 1) return false;

            var bytes = Convert.FromBase64String(b64);
            payload = JsonSerializer.Deserialize<InviteCodePayload>(bytes);
            return payload is not null;
        }
        catch
        {
            return false;
        }
    }
}
