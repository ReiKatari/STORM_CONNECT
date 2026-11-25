using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using StormConnect.Protocol;

namespace StormConnect.Relay;

public sealed class RelayPeerEntry
{
    public required string StormId { get; init; }
    public required WebSocketTransportChannel Channel { get; init; }
    public string HostName { get; set; } = string.Empty;
    public string OsName { get; set; } = string.Empty;
    public int MonitorCount { get; set; } = 1;
    public int DirectPort { get; set; } = ProtocolConstants.DefaultDirectPort;
    public string PublicUdpEndpoint { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public sealed class RelayBridgeSession
{
    public required string Token { get; init; }
    public required string HostStormId { get; init; }
    public required string ClientStormId { get; init; }
    public WebSocket? InitiatorSocket { get; set; }
    public WebSocket? TargetSocket { get; set; }
    public TaskCompletionSource BothConnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}

public sealed class RelayHub
{
    private readonly ConcurrentDictionary<string, RelayPeerEntry> _peers = new();
    private readonly ConcurrentDictionary<string, RelayBridgeSession> _sessions = new();
    private long _totalRelayedBytes;

    public int ActivePeerCount => _peers.Count;
    public int ActiveBridgeCount => _sessions.Count;
    public long TotalRelayedBytes => Interlocked.Read(ref _totalRelayedBytes);

    public async Task HandleControlSocketAsync(WebSocket ws, string remoteIp, CancellationToken ct)
    {
        await using var channel = new WebSocketTransportChannel(ws, ConnectionMode.WssRelay);
        string? registeredId = null;

        try
        {
            while (channel.IsConnected && !ct.IsCancellationRequested)
            {
                var pkt = await channel.ReceivePacketAsync(ct).ConfigureAwait(false);
                if (pkt is null) break;

                switch (pkt.Type)
                {
                    case PacketType.RelayRegister:
                    {
                        var req = FrameCodec.DeserializeJson<RelayRegisterPayload>(pkt.Payload);
                        if (req is null) break;
                        var id = DeviceIdentity.NormalizeId(req.StormId);
                        if (id.Length != 9)
                        {
                            await channel.SendPacketAsync(PacketType.RelayError, System.Text.Encoding.UTF8.GetBytes("Invalid STORM ID"), ct: ct);
                            break;
                        }

                        registeredId = id;
                        var entry = new RelayPeerEntry
                        {
                            StormId = id,
                            Channel = channel,
                            HostName = req.HostName,
                            OsName = req.OsName,
                            MonitorCount = req.MonitorCount,
                            DirectPort = req.DirectPort,
                            PublicUdpEndpoint = !string.IsNullOrWhiteSpace(req.PublicUdpEndpoint) ? req.PublicUdpEndpoint : remoteIp,
                            LastSeenUtc = DateTime.UtcNow
                        };
                        _peers[id] = entry;
                        await channel.SendPacketAsync(PacketType.RelayRegistered, System.Text.Encoding.UTF8.GetBytes(id), ct: ct);
                        break;
                    }

                    case PacketType.RelayConnect:
                    {
                        var req = FrameCodec.DeserializeJson<RelayConnectPayload>(pkt.Payload);
                        if (req is null) break;
                        var fromId = DeviceIdentity.NormalizeId(req.FromStormId);
                        var targetId = DeviceIdentity.NormalizeId(req.TargetStormId);

                        if (!_peers.TryGetValue(targetId, out var targetPeer) || !targetPeer.Channel.IsConnected)
                        {
                            await channel.SendPacketAsync(PacketType.RelayError, System.Text.Encoding.UTF8.GetBytes($"Peer {targetId} is offline"), ct: ct);
                            break;
                        }

                        var token = Guid.NewGuid().ToString("N");
                        var session = new RelayBridgeSession
                        {
                            Token = token,
                            HostStormId = targetId,
                            ClientStormId = fromId
                        };
                        _sessions[token] = session;

                        var fromPeerUdp = _peers.TryGetValue(fromId, out var fp) ? fp.PublicUdpEndpoint : remoteIp;
                        var toInitiator = new RelayPairedPayload(token, targetId, targetPeer.PublicUdpEndpoint, IsInitiator: true);
                        var toTarget = new RelayPairedPayload(token, fromId, fromPeerUdp, IsInitiator: false);

                        await channel.SendPacketAsync(PacketType.RelayPaired, FrameCodec.SerializeJson(toInitiator), ct: ct);
                        await targetPeer.Channel.SendPacketAsync(PacketType.RelayPaired, FrameCodec.SerializeJson(toTarget), ct: ct);
                        break;
                    }

                    case PacketType.Ping:
                    {
                        if (registeredId is not null && _peers.TryGetValue(registeredId, out var p))
                            p.LastSeenUtc = DateTime.UtcNow;
                        await channel.SendPacketAsync(PacketType.Pong, pkt.Payload, ct: ct);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            if (registeredId is not null && _peers.TryGetValue(registeredId, out var current) && ReferenceEquals(current.Channel, channel))
            {
                _peers.TryRemove(registeredId, out _);
            }
        }
    }

    public async Task HandleSessionPipeAsync(WebSocket ws, string token, string role, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(token, out var session))
        {
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unknown session token", CancellationToken.None);
            return;
        }

        bool isInitiator = string.Equals(role, "initiator", StringComparison.OrdinalIgnoreCase);
        lock (session)
        {
            if (isInitiator) session.InitiatorSocket = ws;
            else session.TargetSocket = ws;
            if (session.InitiatorSocket is not null && session.TargetSocket is not null)
                session.BothConnected.TrySetResult();
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
            await session.BothConnected.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);

            var peerSocket = isInitiator ? session.TargetSocket! : session.InitiatorSocket!;
            await ForwardOpaqueFramesAsync(ws, peerSocket, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _sessions.TryRemove(token, out _);
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch { }
            var other = isInitiator ? session.TargetSocket : session.InitiatorSocket;
            try { if (other is not null && other.State == WebSocketState.Open) await other.CloseAsync(WebSocketCloseStatus.NormalClosure, "peer_closed", CancellationToken.None); } catch { }
        }
    }

    private async Task ForwardOpaqueFramesAsync(WebSocket src, WebSocket dst, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (src.State == WebSocketState.Open && dst.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var res = await src.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (res.MessageType == WebSocketMessageType.Close) break;
                if (res.Count > 0)
                {
                    Interlocked.Add(ref _totalRelayedBytes, res.Count);
                    await dst.SendAsync(buffer.AsMemory(0, res.Count), res.MessageType, res.EndOfMessage, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
