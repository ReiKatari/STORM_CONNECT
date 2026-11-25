using StormConnect.Protocol;
using StormConnect.Relay;

var port = ProtocolConstants.DefaultRelayPort;
var udpPort = ProtocolConstants.DefaultUdpPunchPort;

for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var p)) port = p;
    if (args[i] == "--udp-port" && int.TryParse(args[i + 1], out var u)) udpPort = u;
}

if (int.TryParse(Environment.GetEnvironmentVariable("STORM_RELAY_PORT"), out var envPort)) port = envPort;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Services.AddSingleton<RelayHub>();
builder.Services.AddHostedService(sp => new UdpSignalingService(udpPort, sp.GetService<ILogger<UdpSignalingService>>()));

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/health", (RelayHub hub) => Results.Ok(new
{
    service = "STORM CONNECT Relay",
    version = "1.3.2",
    peersOnline = hub.ActivePeerCount,
    activeSessions = hub.ActiveBridgeCount,
    relayedBytes = hub.TotalRelayedBytes,
    utc = DateTime.UtcNow
}));

async Task HandleWs(HttpContext ctx, RelayHub hub)
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("WebSocket required");
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var remoteIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    await hub.HandleControlSocketAsync(ws, remoteIp, ctx.RequestAborted);
}

app.Map("/connect-relay", HandleWs);
app.Map("/ws", HandleWs);

app.Map("/session/{token}", async (HttpContext ctx, string token, RelayHub hub) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("WebSocket required");
        return;
    }
    var role = ctx.Request.Query["role"].ToString();
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.HandleSessionPipeAsync(ws, token, role, ctx.RequestAborted);
});

app.Run();
