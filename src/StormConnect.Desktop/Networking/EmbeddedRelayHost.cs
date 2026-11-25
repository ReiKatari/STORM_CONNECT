using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StormConnect.Protocol;
using StormConnect.Relay;

namespace StormConnect.Desktop.Networking;

/// <summary>
/// Optional in-process Relay & UDP Signaling hub so any STORM CONNECT desktop instance
/// can act as a zero-config team relay server on LAN/VPN/Internet with one toggle.
/// </summary>
public sealed class EmbeddedRelayHost : IAsyncDisposable
{
    private WebApplication? _app;
    private RelayHub? _hub;
    private UdpSignalingService? _udpService;
    private CancellationTokenSource? _udpCts;

    public bool IsRunning => _app is not null;
    public int Port { get; private set; } = ProtocolConstants.DefaultRelayPort;
    public int ActivePeers => _hub?.ActivePeerCount ?? 0;
    public int ActiveSessions => _hub?.ActiveBridgeCount ?? 0;
    public long RelayedBytes => _hub?.TotalRelayedBytes ?? 0;

    public async Task StartAsync(int port = ProtocolConstants.DefaultRelayPort, int udpPort = ProtocolConstants.DefaultUdpPunchPort)
    {
        if (_app is not null) return;
        Port = port;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Services.AddSingleton<RelayHub>();

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        _hub = app.Services.GetRequiredService<RelayHub>();

        app.MapGet("/health", (RelayHub hub) => Results.Ok(new
        {
            service = "STORM CONNECT Embedded Relay",
            version = "1.0.0",
            peersOnline = hub.ActivePeerCount,
            activeSessions = hub.ActiveBridgeCount,
            relayedBytes = hub.TotalRelayedBytes
        }));

        async Task HandleWs(HttpContext ctx, RelayHub hub)
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.HandleControlSocketAsync(ws, ctx.Connection.RemoteIpAddress?.ToString() ?? "", ctx.RequestAborted);
        }

        app.Map("/connect-relay", HandleWs);
        app.Map("/ws", HandleWs);
        app.Map("/session/{token}", async (HttpContext ctx, string token, RelayHub hub) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.HandleSessionPipeAsync(ws, token, ctx.Request.Query["role"].ToString(), ctx.RequestAborted);
        });

        await app.StartAsync().ConfigureAwait(false);
        _app = app;

        _udpCts = new CancellationTokenSource();
        _udpService = new UdpSignalingService(udpPort);
        _ = _udpService.StartAsync(_udpCts.Token);
    }

    public async Task StopAsync()
    {
        if (_udpCts is not null)
        {
            _udpCts.Cancel();
            if (_udpService is not null)
            {
                try { await _udpService.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            _udpCts.Dispose();
            _udpCts = null;
            _udpService = null;
        }

        if (_app is not null)
        {
            try { await _app.StopAsync().ConfigureAwait(false); } catch { }
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
            _hub = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
