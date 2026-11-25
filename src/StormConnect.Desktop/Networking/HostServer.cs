using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StormConnect.Desktop.Engine;
using StormConnect.Protocol;

namespace StormConnect.Desktop.Networking;

public sealed record IncomingConsentRequest(
    string ClientStormId,
    string ClientName,
    string RemoteEndpoint,
    ConnectionMode Mode,
    SessionPermissions ProposedPermissions
);

public sealed class HostSessionContext
{
    public required string SessionId { get; init; }
    public required string ClientName { get; init; }
    public required string ClientStormId { get; init; }
    public required ConnectionMode Mode { get; init; }
    public required SessionPermissions Permissions { get; set; }
    public DateTime ConnectedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Accepts incoming remote control sessions over Direct TCP (port 7440) and WSS Relay,
/// performs ECDH P-256 + AES-256-GCM handshake, authenticates PIN/unattended password,
/// requests explicit interactive Consent when needed, and streams monitors/input/clipboard/files/audio.
/// </summary>
public sealed class HostServer : IAsyncDisposable
{
    private readonly string _stormId;
    private readonly Func<string> _getPin;
    private readonly Func<(string? Hash, byte[]? Salt)> _getUnattendedCredentials;
    private readonly Func<SessionPermissions> _getDefaultPermissions;
    private readonly Func<bool> _getAllowIncoming;
    private readonly Func<IncomingConsentRequest, Task<ConsentDecisionPayload>> _consentPrompt;
    private readonly ClipboardWatcher _clipboard;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _relayCts;
    private int _listenPort = ProtocolConstants.DefaultDirectPort;
    private string _relayUrl = ProtocolConstants.DefaultRelayUrl;
    private readonly ConcurrentDictionary<string, ITransportChannel> _activeClients = new();
    private readonly ConcurrentDictionary<string, HostSessionContext> _activeContexts = new();

    public bool IsRelayConnected { get; private set; }
    public int ActiveClientCount => _activeClients.Count;
    public int ListeningPort { get; private set; }
    public string? ActiveRelayEndpoint { get; private set; }

    public event Action<bool>? RelayStatusChanged;
    public event Action<HostSessionContext>? ClientConnected;
    public event Action<string>? ClientDisconnected;
    public event Action<ChatMessagePayload>? ChatReceived;
    public event Action<string>? AuditLogEvent;

    public HostServer(
        string stormId,
        Func<string> getPin,
        Func<(string? Hash, byte[]? Salt)> getUnattendedCredentials,
        Func<SessionPermissions> getDefaultPermissions,
        Func<bool> getAllowIncoming,
        Func<IncomingConsentRequest, Task<ConsentDecisionPayload>> consentPrompt,
        ClipboardWatcher clipboard)
    {
        _stormId = DeviceIdentity.NormalizeId(stormId);
        _getPin = getPin;
        _getUnattendedCredentials = getUnattendedCredentials;
        _getDefaultPermissions = getDefaultPermissions;
        _getAllowIncoming = getAllowIncoming;
        _consentPrompt = consentPrompt;
        _clipboard = clipboard;

        _clipboard.LocalClipboardChanged += OnLocalClipboardChanged;
    }

    public void Start(int directPort, string relayUrl, bool enableRelay)
    {
        _listenPort = directPort;
        _relayUrl = relayUrl;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        Task.Run(() => AcceptTcpLoopAsync(ct), ct);
        if (enableRelay)
            StartRelayLoop(relayUrl);
    }

    public void UpdateRelay(string relayUrl, bool enableRelay)
    {
        _relayUrl = relayUrl;
        _relayCts?.Cancel();
        _relayCts?.Dispose();
        _relayCts = null;
        IsRelayConnected = false;
        ActiveRelayEndpoint = null;
        RelayStatusChanged?.Invoke(false);

        if (enableRelay && _cts is not null)
            StartRelayLoop(relayUrl);
    }

    private void StartRelayLoop(string relayUrl)
    {
        _relayCts = CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token);
        var ct = _relayCts.Token;
        if (string.IsNullOrWhiteSpace(relayUrl))
            Task.Run(() => PublicMqttMeshControlLoopAsync(ct), ct);
        else
            Task.Run(() => RelayControlLoopAsync(relayUrl, ct), ct);
    }

    private async Task AcceptTcpLoopAsync(CancellationToken ct)
    {
        try
        {
            try
            {
                _listener = TcpListener.Create(_listenPort);
            }
            catch
            {
                _listener = new TcpListener(IPAddress.Any, _listenPort);
            }
            _listener.Start();
            ListeningPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            AuditLogEvent?.Invoke($"Direct TCP host listening on port {ListeningPort}");

            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "LAN";
                var mode = IsPrivateIp(((IPEndPoint?)client.Client.RemoteEndPoint)?.Address)
                    ? ConnectionMode.LanDirect
                    : ConnectionMode.DirectIp;
                var channel = new TcpTransportChannel(client, mode);
                _ = Task.Run(() => HandleIncomingChannelAsync(channel, remoteEp, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AuditLogEvent?.Invoke($"Direct TCP listener stopped: {ex.Message}");
        }
    }

    private async Task PublicMqttMeshControlLoopAsync(CancellationToken ct)
    {
        int brokerIdx = 0;
        int backoffMs = 1500;

        while (!ct.IsCancellationRequested)
        {
            string brokerUrl = ProtocolConstants.PublicMqttWssBrokers[brokerIdx % ProtocolConstants.PublicMqttWssBrokers.Length];
            await using var mqtt = new MqttWssClient();
            try
            {
                string cid = $"sc_h_{_stormId}_{Guid.NewGuid().ToString("N")[..6]}";
                await mqtt.ConnectAsync(brokerUrl, cid, ct).ConfigureAwait(false);
                await mqtt.SubscribeAsync($"sc/v1/s/{_stormId}", ct).ConfigureAwait(false);

                IsRelayConnected = true;
                ActiveRelayEndpoint = brokerUrl;
                RelayStatusChanged?.Invoke(true);
                backoffMs = 1500;

                while (mqtt.IsConnected && !ct.IsCancellationRequested)
                {
                    var msg = await mqtt.ReadMessageAsync(ct).ConfigureAwait(false);
                    if (msg is null) break;

                    try
                    {
                        using var doc = JsonDocument.Parse(msg.Value.Payload);
                        var token = doc.RootElement.TryGetProperty("Token", out var tp) ? tp.GetString() : null;
                        var fromId = doc.RootElement.TryGetProperty("FromStormId", out var fp) ? fp.GetString() : "Peer";
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            _ = Task.Run(() => HandlePublicMeshSessionAsync(brokerUrl, token!, fromId ?? "Peer", ct), ct);
                        }
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
            finally
            {
                IsRelayConnected = false;
                ActiveRelayEndpoint = null;
                RelayStatusChanged?.Invoke(false);
            }

            brokerIdx++;
            try
            {
                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                backoffMs = Math.Min(15_000, backoffMs * 2);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task HandlePublicMeshSessionAsync(string brokerUrl, string token, string peerStormId, CancellationToken ct)
    {
        try
        {
            var mqtt = new MqttWssClient();
            string cid = $"sc_hs_{_stormId}_{Guid.NewGuid().ToString("N")[..6]}";
            await mqtt.ConnectAsync(brokerUrl, cid, ct).ConfigureAwait(false);
            var pipe = new MqttPipeTransportChannel(mqtt, token, isInitiator: false);
            await pipe.SubscribeRxAsync(ct).ConfigureAwait(false);
            await HandleIncomingChannelAsync(pipe, $"Mesh:{peerStormId}", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AuditLogEvent?.Invoke($"Public mesh session error: {ex.Message}");
        }
    }

    private async Task RelayControlLoopAsync(string relayUrl, CancellationToken ct)
    {
        int backoffMs = 1500;
        while (!ct.IsCancellationRequested)
        {
            ClientWebSocket? ws = null;
            try
            {
                ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                await ws.ConnectAsync(new Uri(relayUrl), ct).ConfigureAwait(false);

                await using var ch = new WebSocketTransportChannel(ws, ConnectionMode.WssRelay);
                var reg = new RelayRegisterPayload(
                    _stormId,
                    Environment.MachineName,
                    "Windows",
                    DxgiScreenCapturer.GetMonitors().Count(m => m.Index >= 0),
                    _listenPort,
                    null);
                await ch.SendPacketAsync(PacketType.RelayRegister, FrameCodec.SerializeJson(reg), ct: ct).ConfigureAwait(false);

                backoffMs = 1500;
                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pingTask = Task.Run(async () =>
                {
                    while (!pingCts.IsCancellationRequested)
                    {
                        await Task.Delay(15000, pingCts.Token).ConfigureAwait(false);
                        await ch.SendPacketAsync(PacketType.Ping, Array.Empty<byte>(), ct: pingCts.Token).ConfigureAwait(false);
                    }
                }, pingCts.Token);

                while (ch.IsConnected && !ct.IsCancellationRequested)
                {
                    var pkt = await ch.ReceivePacketAsync(ct).ConfigureAwait(false);
                    if (pkt is null) break;

                    if (pkt.Type == PacketType.RelayRegistered)
                    {
                        IsRelayConnected = true;
                        RelayStatusChanged?.Invoke(true);
                    }
                    else if (pkt.Type == PacketType.RelayPaired)
                    {
                        var paired = FrameCodec.DeserializeJson<RelayPairedPayload>(pkt.Payload);
                        if (paired is not null)
                            _ = Task.Run(() => ConnectRelaySessionPipeAsync(relayUrl, paired, ct), ct);
                    }
                }
                pingCts.Cancel();
            }
            catch (OperationCanceledException) { break; }
            catch { }
            finally
            {
                IsRelayConnected = false;
                RelayStatusChanged?.Invoke(false);
                ws?.Dispose();
            }

            try
            {
                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                backoffMs = Math.Min(20_000, backoffMs * 2);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConnectRelaySessionPipeAsync(string controlRelayUrl, RelayPairedPayload paired, CancellationToken ct)
    {
        try
        {
            var baseUri = new Uri(controlRelayUrl);
            var builder = new UriBuilder(baseUri)
            {
                Path = $"/session/{paired.SessionToken}",
                Query = "role=target"
            };
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            await ws.ConnectAsync(builder.Uri, ct).ConfigureAwait(false);
            var ch = new WebSocketTransportChannel(ws, ConnectionMode.WssRelay);
            await HandleIncomingChannelAsync(ch, $"Relay:{paired.PeerStormId}", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AuditLogEvent?.Invoke($"Relay session error: {ex.Message}");
        }
    }

    private async Task HandleIncomingChannelAsync(ITransportChannel rawChannel, string remoteEndpoint, CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..12];
        ITransportChannel channel = rawChannel;
        CryptoSession? crypto = null;

        try
        {
            if (!_getAllowIncoming())
            {
                await rawChannel.SendPacketAsync(PacketType.Disconnect, Encoding.UTF8.GetBytes("Incoming connections disabled"), ct: ct);
                return;
            }

            // 1. KeyExchange (Host acts as responder: isInitiator = false)
            crypto = new CryptoSession();
            await rawChannel.SendPacketAsync(PacketType.KeyExchange, crypto.LocalPublicKeyUncompressed, ct: ct).ConfigureAwait(false);

            var peerKeyPkt = await rawChannel.ReceivePacketAsync(ct).ConfigureAwait(false);
            if (peerKeyPkt is null || peerKeyPkt.Type != PacketType.KeyExchange)
                return;
            crypto.DeriveKeys(peerKeyPkt.Payload, isInitiator: false);

            channel = new EncryptedTransportChannel(rawChannel, crypto);
            crypto = null; // Ownership transferred to EncryptedTransportChannel

            // 2. Send Hello + AuthChallenge
            var monitors = DxgiScreenCapturer.GetMonitors();
            var hello = new HelloPayload(_stormId, Environment.MachineName, "Windows", "1.2.0", monitors.Count(m => m.Index >= 0), true);
            await channel.SendPacketAsync(PacketType.Hello, FrameCodec.SerializeJson(hello), ct: ct).ConfigureAwait(false);

            var pinSalt = RandomNumberGenerator.GetBytes(16);
            var challengeNonce = RandomNumberGenerator.GetBytes(32);
            var (unattHash, unattSalt) = _getUnattendedCredentials();
            var challenge = new AuthChallengePayload(
                unattSalt ?? pinSalt,
                challengeNonce,
                AllowPinConsent: true,
                AllowUnattended: !string.IsNullOrWhiteSpace(unattHash) && unattSalt is not null);
            await channel.SendPacketAsync(PacketType.AuthRequest, FrameCodec.SerializeJson(challenge), ct: ct).ConfigureAwait(false);

            // 3. Receive AuthRequest from Client
            var authPkt = await channel.ReceivePacketAsync(ct).ConfigureAwait(false);
            if (authPkt is null || authPkt.Type != PacketType.AuthRequest) return;
            var authReq = FrameCodec.DeserializeJson<AuthRequestPayload>(authPkt.Payload);
            if (authReq is null) return;

            var perms = _getDefaultPermissions().Clone();
            bool authorized = false;

            if (authReq.Mode == AuthMode.UnattendedPassword && !string.IsNullOrWhiteSpace(unattHash))
            {
                authorized = CryptoSession.VerifyChallengeProof(unattHash, challengeNonce, authReq.PinOrPasswordProof);
            }
            else if (!string.IsNullOrWhiteSpace(authReq.PinOrPasswordProof))
            {
                var pinHash = CryptoSession.HashPassword(_getPin(), challenge.Salt);
                authorized = CryptoSession.VerifyChallengeProof(pinHash, challengeNonce, authReq.PinOrPasswordProof);
            }

            if (!authorized)
            {
                // Ask local user via Consent Modal
                await channel.SendPacketAsync(
                    PacketType.AuthResponse,
                    FrameCodec.SerializeJson(new AuthResponsePayload(false, true, "Waiting for host approval...", perms)),
                    ct: ct).ConfigureAwait(false);

                var decision = await _consentPrompt(new IncomingConsentRequest(
                    authReq.ClientStormId,
                    authReq.ClientName,
                    remoteEndpoint,
                    channel.Mode,
                    perms)).ConfigureAwait(false);

                if (!decision.Accepted)
                {
                    await channel.SendPacketAsync(
                        PacketType.AuthResponse,
                        FrameCodec.SerializeJson(new AuthResponsePayload(false, false, "Declined by host user.", perms)),
                        ct: ct).ConfigureAwait(false);
                    return;
                }
                perms = decision.Permissions;
            }

            await channel.SendPacketAsync(
                PacketType.AuthResponse,
                FrameCodec.SerializeJson(new AuthResponsePayload(true, false, "Connected", perms)),
                ct: ct).ConfigureAwait(false);

            var ctx = new HostSessionContext
            {
                SessionId = sessionId,
                ClientName = authReq.ClientName,
                ClientStormId = authReq.ClientStormId,
                Mode = channel.Mode,
                Permissions = perms
            };
            _activeClients[sessionId] = channel;
            _activeContexts[sessionId] = ctx;
            ClientConnected?.Invoke(ctx);
            AuditLogEvent?.Invoke($"Session started: {authReq.ClientName} ({authReq.ClientStormId}) via {channel.Mode}");

            await channel.SendPacketAsync(PacketType.MonitorList, FrameCodec.SerializeJson(monitors), ct: ct).ConfigureAwait(false);
            await RunHostSessionLoopAsync(channel, ctx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AuditLogEvent?.Invoke($"Host session ended ({sessionId}): {ex.Message}");
        }
        finally
        {
            crypto?.Dispose();
            _activeClients.TryRemove(sessionId, out _);
            _activeContexts.TryRemove(sessionId, out _);
            ClientDisconnected?.Invoke(sessionId);
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunHostSessionLoopAsync(ITransportChannel channel, HostSessionContext ctx, CancellationToken parentCt)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        var ct = sessionCts.Token;

        int activeMonitor = 0;
        bool forceKeyFrame = true;
        int targetQuality = 72;
        using var capturer = new DxgiScreenCapturer();
        var encoder = new TileDeltaEncoder();
        using var audio = new AudioStreamer();

        if (ctx.Permissions.AllowAudio)
        {
            audio.AudioFrameCaptured += async pcm =>
            {
                if (ctx.Permissions.AllowAudio && channel.IsConnected)
                {
                    try { await channel.SendPacketAsync(PacketType.AudioFrame, pcm, compress: true, ct).ConfigureAwait(false); } catch { }
                }
            };
            audio.StartCapture();
        }

        var activeUploads = new ConcurrentDictionary<string, (FileStream Stream, long TotalBytes, long Received, string Path, string ExpectedSha)>();

        // Video streaming loop
        var videoTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && channel.IsConnected)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var frame = capturer.Capture(Volatile.Read(ref activeMonitor));
                    if (frame is not null)
                    {
                        bool key = forceKeyFrame;
                        forceKeyFrame = false;
                        var update = encoder.Encode(frame, targetQuality, key);
                        if (update is not null)
                        {
                            var bytes = TileDeltaEncoder.SerializeVideoUpdate(update);
                            await channel.SendPacketAsync(
                                update.IsFullFrame ? PacketType.VideoFrameFull : PacketType.VideoFrameTiles,
                                bytes,
                                compress: false,
                                ct).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { break; }

                int elapsed = (int)sw.ElapsedMilliseconds;
                int targetInterval = channel.Mode == ConnectionMode.WssRelay ? 33 : 20; // 30-50 FPS smooth adaptive
                if (elapsed < targetInterval)
                {
                    try { await Task.Delay(targetInterval - elapsed, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, ct);

        try
        {
            while (!ct.IsCancellationRequested && channel.IsConnected)
            {
                var pkt = await channel.ReceivePacketAsync(ct).ConfigureAwait(false);
                if (pkt is null || pkt.Type == PacketType.Disconnect) break;

                switch (pkt.Type)
                {
                    case PacketType.MonitorSelect:
                        if (pkt.Payload.Length >= 4)
                        {
                            int mon = BitConverter.ToInt32(pkt.Payload, 0);
                            Interlocked.Exchange(ref activeMonitor, mon);
                            if (pkt.Payload.Length >= 8)
                                targetQuality = Math.Clamp(BitConverter.ToInt32(pkt.Payload, 4), 25, 95);
                            encoder.Reset();
                            forceKeyFrame = true;
                        }
                        break;

                    case PacketType.InputMouse:
                        if (ctx.Permissions.AllowControl && pkt.Payload.Length >= 20)
                        {
                            double nx = BitConverter.ToDouble(pkt.Payload, 0);
                            double ny = BitConverter.ToDouble(pkt.Payload, 8);
                            byte btn = pkt.Payload[16];
                            byte act = pkt.Payload[17];
                            short wheel = BitConverter.ToInt16(pkt.Payload, 18);
                            NativeInputInjector.InjectMouse(new MouseEventData(nx, ny, btn, act, wheel), Volatile.Read(ref activeMonitor));
                        }
                        break;

                    case PacketType.InputKeyboard:
                        if (ctx.Permissions.AllowControl && pkt.Payload.Length >= 8)
                        {
                            ushort vk = BitConverter.ToUInt16(pkt.Payload, 0);
                            ushort scan = BitConverter.ToUInt16(pkt.Payload, 2);
                            bool isUp = pkt.Payload[4] != 0;
                            bool isExt = pkt.Payload[5] != 0;
                            ushort uChar = BitConverter.ToUInt16(pkt.Payload, 6);
                            NativeInputInjector.InjectKey(new KeyEventData(vk, scan, isUp, isExt, uChar == 0 ? null : (char)uChar));
                        }
                        break;

                    case PacketType.SessionAction:
                        if (pkt.Payload.Length >= 1)
                        {
                            var action = (SystemActionKind)pkt.Payload[0];
                            if (action == SystemActionKind.RequestKeyFrame)
                            {
                                forceKeyFrame = true;
                            }
                            else if (ctx.Permissions.AllowSystemActions)
                            {
                                NativeInputInjector.ExecuteSystemAction(action);
                            }
                        }
                        break;

                    case PacketType.ClipboardData:
                        if (ctx.Permissions.AllowClipboard)
                        {
                            var cb = FrameCodec.DeserializeJson<ClipboardPayload>(pkt.Payload);
                            if (cb is not null) _clipboard.ApplyRemoteClipboard(cb);
                        }
                        break;

                    case PacketType.FileListReq:
                        if (ctx.Permissions.AllowFileTransfer)
                        {
                            var reqPath = Encoding.UTF8.GetString(pkt.Payload);
                            var list = FileSystemHandler.ListDirectory(reqPath);
                            await channel.SendPacketAsync(PacketType.FileListResp, FrameCodec.SerializeJson(list), compress: true, ct).ConfigureAwait(false);
                        }
                        break;

                    case PacketType.FileOperationReq:
                        if (ctx.Permissions.AllowFileTransfer)
                        {
                            var op = FrameCodec.DeserializeJson<FileOperationRequestPayload>(pkt.Payload);
                            if (op is not null)
                            {
                                var res = FileSystemHandler.ExecuteOperation(op);
                                await channel.SendPacketAsync(PacketType.FileOperationResp, FrameCodec.SerializeJson(res), ct: ct).ConfigureAwait(false);
                            }
                        }
                        break;

                    case PacketType.FileTransferInit:
                        if (ctx.Permissions.AllowFileTransfer)
                        {
                            var init = FrameCodec.DeserializeJson<FileTransferInitPayload>(pkt.Payload);
                            if (init is not null)
                            {
                                if (init.IsUploadToRemote)
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(init.RemotePath) ?? ".");
                                    var fs = new FileStream(init.RemotePath, FileMode.Create, FileAccess.Write, FileShare.None);
                                    activeUploads[init.TransferId] = (fs, init.TotalBytes, 0, init.RemotePath, init.Sha256Hex);
                                }
                                else
                                {
                                    _ = Task.Run(() => StreamFileToClientAsync(channel, init, ct), ct);
                                }
                            }
                        }
                        break;

                    case PacketType.FileTransferChunk:
                        if (ctx.Permissions.AllowFileTransfer && pkt.Payload.Length >= 36)
                        {
                            var tid = Encoding.ASCII.GetString(pkt.Payload, 0, 32);
                            int dataLen = BitConverter.ToInt32(pkt.Payload, 32);
                            if (activeUploads.TryGetValue(tid, out var st))
                            {
                                await st.Stream.WriteAsync(pkt.Payload.AsMemory(36, dataLen), ct).ConfigureAwait(false);
                                long rec = st.Received + dataLen;
                                activeUploads[tid] = (st.Stream, st.TotalBytes, rec, st.Path, st.ExpectedSha);
                                if (rec >= st.TotalBytes)
                                {
                                    await st.Stream.DisposeAsync().ConfigureAwait(false);
                                    activeUploads.TryRemove(tid, out _);
                                    var ack = new FileTransferAckPayload(tid, true, rec, "Uploaded");
                                    await channel.SendPacketAsync(PacketType.FileTransferAck, FrameCodec.SerializeJson(ack), ct: ct).ConfigureAwait(false);
                                }
                            }
                        }
                        break;

                    case PacketType.ChatMessage:
                        var chat = FrameCodec.DeserializeJson<ChatMessagePayload>(pkt.Payload);
                        if (chat is not null) ChatReceived?.Invoke(chat);
                        break;

                    case PacketType.Ping:
                        await channel.SendPacketAsync(PacketType.Pong, pkt.Payload, ct: ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        finally
        {
            sessionCts.Cancel();
            foreach (var up in activeUploads.Values)
            {
                try { await up.Stream.DisposeAsync(); } catch { }
            }
            try { await videoTask; } catch { }
        }
    }

    private static async Task StreamFileToClientAsync(ITransportChannel channel, FileTransferInitPayload init, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(init.RemotePath))
            {
                await channel.SendPacketAsync(PacketType.FileTransferAck,
                    FrameCodec.SerializeJson(new FileTransferAckPayload(init.TransferId, false, 0, "File not found")), ct: ct);
                return;
            }

            var fi = new FileInfo(init.RemotePath);
            string shaHex;
            await using (var hashFs = File.OpenRead(init.RemotePath))
                shaHex = Convert.ToHexString(await SHA256.HashDataAsync(hashFs, ct).ConfigureAwait(false));

            var respInit = init with { TotalBytes = fi.Length, Sha256Hex = shaHex };
            await channel.SendPacketAsync(PacketType.FileTransferInit, FrameCodec.SerializeJson(respInit), ct: ct).ConfigureAwait(false);

            var tidBytes = Encoding.ASCII.GetBytes(init.TransferId.PadRight(32)[..32]);
            var buffer = new byte[ProtocolConstants.FileChunkBytes + 36];
            Buffer.BlockCopy(tidBytes, 0, buffer, 0, 32);

            await using var fs = new FileStream(init.RemotePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int read;
            while ((read = await fs.ReadAsync(buffer.AsMemory(36, ProtocolConstants.FileChunkBytes), ct).ConfigureAwait(false)) > 0)
            {
                BitConverter.TryWriteBytes(buffer.AsSpan(32, 4), read);
                await channel.SendPacketAsync(PacketType.FileTransferChunk, buffer.AsMemory(0, 36 + read), compress: false, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            try
            {
                await channel.SendPacketAsync(PacketType.FileTransferAck,
                    FrameCodec.SerializeJson(new FileTransferAckPayload(init.TransferId, false, 0, ex.Message)), ct: ct);
            }
            catch { }
        }
    }

    private void OnLocalClipboardChanged(ClipboardPayload payload)
    {
        var json = FrameCodec.SerializeJson(payload);
        foreach (var kv in _activeClients)
        {
            if (_activeContexts.TryGetValue(kv.Key, out var ctx) && ctx.Permissions.AllowClipboard)
            {
                _ = Task.Run(async () =>
                {
                    try { await kv.Value.SendPacketAsync(PacketType.ClipboardData, json, compress: true).ConfigureAwait(false); } catch { }
                });
            }
        }
    }

    public async Task SendChatAsync(string text)
    {
        var msg = new ChatMessagePayload(Environment.MachineName, text, DateTime.UtcNow);
        var json = FrameCodec.SerializeJson(msg);
        foreach (var ch in _activeClients.Values)
        {
            try { await ch.SendPacketAsync(PacketType.ChatMessage, json).ConfigureAwait(false); } catch { }
        }
    }

    public async Task DisconnectClientAsync(string sessionId)
    {
        if (_activeClients.TryRemove(sessionId, out var ch))
        {
            try { await ch.SendPacketAsync(PacketType.Disconnect, Array.Empty<byte>()).ConfigureAwait(false); } catch { }
            await ch.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool IsPrivateIp(IPAddress? ip)
    {
        if (ip is null) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public async ValueTask DisposeAsync()
    {
        _clipboard.LocalClipboardChanged -= OnLocalClipboardChanged;
        _relayCts?.Cancel();
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        foreach (var ch in _activeClients.Values)
            await ch.DisposeAsync().ConfigureAwait(false);
        _activeClients.Clear();
    }
}
