using System.Net;
using System.Net.Sockets;
using System.Text;
using StormConnect.Protocol;

namespace StormConnect.Relay;

/// <summary>
/// Lightweight UDP endpoint reflector (STUN-like) so peers discover their public NAT IP:Port
/// and exchange punch packets when attempting direct P2P upgrade.
/// </summary>
public sealed class UdpSignalingService : BackgroundService
{
    private readonly int _port;
    private readonly ILogger<UdpSignalingService>? _logger;

    public UdpSignalingService(int port = ProtocolConstants.DefaultUdpPunchPort, ILogger<UdpSignalingService>? logger = null)
    {
        _port = port;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
            _logger?.LogInformation("STORM CONNECT UDP signaling listening on :{Port}", _port);

            while (!stoppingToken.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(stoppingToken).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (text.StartsWith("SCON_PROBE", StringComparison.Ordinal))
                {
                    var reply = Encoding.UTF8.GetBytes($"SCON_ENDPOINT|{result.RemoteEndPoint.Address}:{result.RemoteEndPoint.Port}");
                    await udp.SendAsync(reply, reply.Length, result.RemoteEndPoint).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "UDP signaling stopped on port {Port}", _port);
        }
        finally
        {
            udp?.Dispose();
        }
    }
}
