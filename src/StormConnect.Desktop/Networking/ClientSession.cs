using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StormConnect.Desktop.Engine;
using StormConnect.Protocol;

namespace StormConnect.Desktop.Networking;

public sealed record FileTransferProgress(
    string TransferId,
    string FileName,
    bool IsUpload,
    long BytesDone,
    long TotalBytes,
    double SpeedMbps,
    bool Completed,
    string? Error
);

public sealed class DualAuthContext
{
    public required string HostName { get; init; }
    public required bool AllowUnattended { get; init; }
    public required Func<string, Task> SubmitSecretAsync { get; init; }
    public required Action CancelAuth { get; init; }
    public required CancellationToken CompletedToken { get; init; }
    public event Action<string>? ErrorReceived;
    internal void RaiseError(string message) => ErrorReceived?.Invoke(message);
}

/// <summary>
/// Remote viewer & controller session connecting via Direct TCP/LAN or WSS Relay,
/// performing ECDH P-256 + AES-256-GCM handshake and rendering 60 FPS video directly into WPF WriteableBitmap.
/// </summary>
public sealed class ClientSession : IAsyncDisposable
{
    private readonly string _localStormId;
    private readonly ClipboardWatcher _clipboard;
    private ITransportChannel? _channel;
    private CancellationTokenSource? _cts;
    private WriteableBitmap? _bitmap;
    private readonly object _bmpLock = new();
    private readonly AudioStreamer _audio = new();
    private readonly ConcurrentDictionary<string, (FileStream Stream, string LocalPath, string FileName, long Total, long Done, DateTime StartUtc)> _downloads = new();

    private long _bytesRxWindow;
    private int _framesWindow;
    private DateTime _telemetryWindowStart = DateTime.UtcNow;
    private double _lastFps;
    private double _lastMbps;
    private int _lastRttMs;
    private double _lastTileSavings;
    private int _activeMonitorIndex;
    private bool _audioEnabled = true;
    private long _framesReceivedCount;

    public SessionTelemetry Telemetry { get; } = new();
    public SessionPermissions GrantedPermissions { get; private set; } = new();
    public string RemoteHostName { get; private set; } = string.Empty;
    public string RemoteStormId { get; private set; } = string.Empty;
    public IReadOnlyList<MonitorInfo> Monitors { get; private set; } = [];
    public WriteableBitmap? FrameBitmap => _bitmap;
    public bool IsConnected => _channel?.IsConnected == true;
    public bool IsAudioEnabled => _audioEnabled;
    public long FramesReceivedCount => Interlocked.Read(ref _framesReceivedCount);

    public event Action<WriteableBitmap>? FrameUpdated;
    public event Action<int, int>? FrameDimensionsReceived;
    public event Action<IReadOnlyList<MonitorInfo>>? MonitorsUpdated;
    public event Action<SessionTelemetry>? TelemetryUpdated;
    public event Action<FileListResponsePayload>? FileListReceived;
    public event Action<FileTransferProgress>? FileTransferProgressed;
    public event Action<FileOperationResponsePayload>? FileOperationCompleted;
    public event Action<ChatMessagePayload>? ChatReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? Disconnected;

    public ClientSession(string localStormId, ClipboardWatcher clipboard)
    {
        _localStormId = DeviceIdentity.NormalizeId(localStormId);
        _clipboard = clipboard;
        _clipboard.LocalClipboardChanged += OnLocalClipboardChanged;
    }

    public Task ConnectAsync(
        string targetInput,
        string relayUrl,
        IReadOnlyList<DiscoveredDevice> lanDevices,
        Func<string, bool, Task<string?>> promptPasswordOrPin,
        int preferredMethodIndex = 0, // 0=Auto, 1=DirectOnly, 2=InviteCode, 3=RelayOnly
        CancellationToken ct = default)
    {
        return ConnectDualAuthCoreAsync(
            targetInput,
            relayUrl,
            lanDevices,
            savedSecret: null,
            legacyPromptBeforeSend: promptPasswordOrPin,
            onInteractiveAuthNeeded: _ => Task.CompletedTask,
            preferredMethodIndex,
            ct);
    }

    public Task ConnectDualAuthAsync(
        string targetInput,
        string relayUrl,
        IReadOnlyList<DiscoveredDevice> lanDevices,
        string? savedSecret,
        Func<DualAuthContext, Task> onInteractiveAuthNeeded,
        int preferredMethodIndex = 0,
        CancellationToken ct = default)
    {
        return ConnectDualAuthCoreAsync(
            targetInput,
            relayUrl,
            lanDevices,
            savedSecret,
            legacyPromptBeforeSend: null,
            onInteractiveAuthNeeded,
            preferredMethodIndex,
            ct);
    }

    private async Task ConnectDualAuthCoreAsync(
        string targetInput,
        string relayUrl,
        IReadOnlyList<DiscoveredDevice> lanDevices,
        string? savedSecret,
        Func<string, bool, Task<string?>>? legacyPromptBeforeSend,
        Func<DualAuthContext, Task> onInteractiveAuthNeeded,
        int preferredMethodIndex,
        CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        ITransportChannel? rawChannel = null;
        SconPacket? preReadHostKey = null;
        string trimmed = targetInput.Trim();
        string normDigits = DeviceIdentity.NormalizeId(trimmed);

        // 0. Check if target is a serverless Direct Invite Code (SC1-...)
        if (InviteCode.TryDecode(trimmed, out var invite) && invite is not null)
        {
            StatusChanged?.Invoke(Services.LocalizationService.T("StatusConnectingInvite"));
            (rawChannel, preReadHostKey) = await ConnectViaInviteCascadeAsync(invite, lanDevices, relayUrl, preferredMethodIndex == 2, token).ConfigureAwait(false);
        }
        // 1. Check if target is an explicit IPv4/IPv6/hostname
        else if (normDigits.Length != 9 || trimmed.Contains('.') || trimmed.Contains(':'))
        {
            StatusChanged?.Invoke(Services.LocalizationService.T("StatusConnectingDirect"));
            rawChannel = await ConnectDirectTcpAsync(trimmed, ProtocolConstants.DefaultDirectPort, ConnectionMode.DirectIp, token).ConfigureAwait(false);
        }
        else
        {
            // 2. Try LAN Discovery peer first unless Relay-Only is forced
            if (preferredMethodIndex != 3)
            {
                var lanPeer = lanDevices.FirstOrDefault(d => d.StormId == normDigits);
                if (lanPeer is not null)
                {
                    try
                    {
                        StatusChanged?.Invoke($"{Services.LocalizationService.T("StatusConnectingLan")} ({lanPeer.LocalIp})");
                        rawChannel = await ConnectDirectTcpAsync(lanPeer.LocalIp, lanPeer.DirectPort, ConnectionMode.LanDirect, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        rawChannel = null;
                    }
                }
            }

            // 3. Fallback to Custom WSS Relay or Built-in Zero-Server Public MQTT-WSS Mesh
            if (rawChannel is null && preferredMethodIndex != 1)
            {
                StatusChanged?.Invoke(Services.LocalizationService.T("StatusConnectingRelay"));
                (rawChannel, preReadHostKey) = await ConnectViaRelayOrMeshAsync(normDigits, relayUrl, preferredMethodIndex == 3, token).ConfigureAwait(false);
            }
        }

        if (rawChannel is null)
            throw new IOException(Services.LocalizationService.T("StatusConnFailed"));

        // 4. ECDH P-256 + AES-256-GCM Handshake (Client is Initiator; ownership moves to EncryptedTransportChannel)
        var crypto = new CryptoSession();
        try
        {
            var hostKeyPkt = preReadHostKey ?? await rawChannel.ReceivePacketAsync(token).ConfigureAwait(false);
            if (hostKeyPkt is null || hostKeyPkt.Type != PacketType.KeyExchange)
                throw new IOException(Services.LocalizationService.T("StatusPeerNoResponse"));

            await rawChannel.SendPacketAsync(PacketType.KeyExchange, crypto.LocalPublicKeyUncompressed, ct: token).ConfigureAwait(false);
            crypto.DeriveKeys(hostKeyPkt.Payload, isInitiator: true);

            _channel = new EncryptedTransportChannel(rawChannel, crypto);
        }
        catch
        {
            crypto.Dispose();
            await rawChannel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        Telemetry.Mode = _channel.Mode;

        // 5. Read Hello + AuthChallenge
        var helloPkt = await _channel.ReceivePacketAsync(token).ConfigureAwait(false);
        if (helloPkt?.Type == PacketType.Hello)
        {
            var hello = FrameCodec.DeserializeJson<HelloPayload>(helloPkt.Payload);
            if (hello is not null)
            {
                RemoteHostName = hello.HostName;
                RemoteStormId = hello.StormId;
            }
        }

        var chalPkt = await _channel.ReceivePacketAsync(token).ConfigureAwait(false);
        if (chalPkt is null || chalPkt.Type != PacketType.AuthRequest)
            throw new IOException(Services.LocalizationService.T("StatusMissingChallenge"));

        var chal = FrameCodec.DeserializeJson<AuthChallengePayload>(chalPkt.Payload)
            ?? throw new IOException(Services.LocalizationService.T("StatusInvalidChallenge"));

        async Task SendAuthAsync(string? sec)
        {
            AuthMode m = AuthMode.PinOrConsent;
            string? p = null;
            if (!string.IsNullOrWhiteSpace(sec))
            {
                m = chal.AllowUnattended ? AuthMode.UnattendedPassword : AuthMode.PinOrConsent;
                p = CryptoSession.ComputeChallengeProof(sec, chal.Salt, chal.NonceChallenge);
            }
            var req = new AuthRequestPayload(_localStormId, Environment.MachineName, m, p, null);
            await _channel.SendPacketAsync(PacketType.AuthRequest, FrameCodec.SerializeJson(req), ct: token).ConfigureAwait(false);
        }

        if (legacyPromptBeforeSend is not null)
        {
            savedSecret = await legacyPromptBeforeSend(RemoteHostName, chal.AllowUnattended).ConfigureAwait(false);
        }

        // Send initial AuthRequest (saved unattended password if any, or null to open Host Consent + Client PIN simultaneously)
        await SendAuthAsync(savedSecret).ConfigureAwait(false);

        using var authDoneCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        DualAuthContext? dualCtx = null;
        bool interactiveStarted = false;

        try
        {
            while (true)
            {
                var respPkt = await _channel.ReceivePacketAsync(token).ConfigureAwait(false);
                if (respPkt is null) throw new IOException(Services.LocalizationService.T("StatusClosedBeforeAuth"));
                if (respPkt.Type != PacketType.AuthResponse) continue;

                var resp = FrameCodec.DeserializeJson<AuthResponsePayload>(respPkt.Payload);
                if (resp is null) throw new IOException(Services.LocalizationService.T("StatusInvalidAuthResp"));
                if (resp.PendingConsent)
                {
                    StatusChanged?.Invoke(Services.LocalizationService.T("StatusWaitingConsent"));
                    if (!interactiveStarted)
                    {
                        interactiveStarted = true;
                        dualCtx = new DualAuthContext
                        {
                            HostName = RemoteHostName,
                            AllowUnattended = chal.AllowUnattended,
                            SubmitSecretAsync = SendAuthAsync,
                            CancelAuth = () => { try { _cts?.Cancel(); } catch { } },
                            CompletedToken = authDoneCts.Token
                        };
                        _ = Task.Run(async () =>
                        {
                            try { await onInteractiveAuthNeeded(dualCtx).ConfigureAwait(false); }
                            catch { }
                        }, authDoneCts.Token);
                    }
                    else if (!string.IsNullOrWhiteSpace(resp.Reason))
                    {
                        dualCtx?.RaiseError(resp.Reason);
                    }
                    continue;
                }
                if (!resp.Accepted)
                    throw new UnauthorizedAccessException(resp.Reason);

                GrantedPermissions = resp.Permissions;
                break;
            }
        }
        finally
        {
            await authDoneCts.CancelAsync().ConfigureAwait(false);
        }

        _audioEnabled = _channel.Mode != ConnectionMode.WssRelay;
        if (GrantedPermissions.AllowAudio && _audioEnabled)
            _audio.StartPlayback();

        Services.SessionLogger.Log("CLIENT", $"Authenticated to {RemoteHostName} via {_channel.Mode}");
        StatusChanged?.Invoke(string.Format(Services.LocalizationService.T("StatusConnectedTo"), RemoteHostName));
        _ = Task.Run(() => ReceiveLoopAsync(token), token);
        _ = Task.Run(() => PingLoopAsync(token), token);
        _ = Task.Run(() => KeyframeWatchdogAsync(token), token);
    }

    private async Task KeyframeWatchdogAsync(CancellationToken ct)
    {
        try
        {
            int[] delaysMs = [800, 1400, 2200];
            foreach (int d in delaysMs)
            {
                await Task.Delay(d, ct).ConfigureAwait(false);
                if (FramesReceivedCount == 0 && IsConnected)
                    await SendSystemActionAsync(SystemActionKind.RequestKeyFrame).ConfigureAwait(false);
                else
                    break;
            }
        }
        catch { }
    }

    private async Task<(ITransportChannel? Channel, SconPacket? HostKey)> ConnectViaInviteCascadeAsync(
        InviteCodePayload invite,
        IReadOnlyList<DiscoveredDevice> lanDevices,
        string relayUrl,
        bool directOnly,
        CancellationToken ct)
    {
        var candidates = new List<(string Host, int Port, ConnectionMode Mode)>();
        var lanMatch = lanDevices.FirstOrDefault(d => d.StormId == DeviceIdentity.NormalizeId(invite.StormId));
        if (lanMatch is not null)
            candidates.Add((lanMatch.LocalIp, lanMatch.DirectPort, ConnectionMode.LanDirect));
        if (!string.IsNullOrWhiteSpace(invite.LanIp))
            candidates.Add((invite.LanIp, invite.DirectPort, ConnectionMode.LanDirect));
        if (!string.IsNullOrWhiteSpace(invite.VpnIp))
            candidates.Add((invite.VpnIp, invite.DirectPort, ConnectionMode.DirectIp));
        if (!string.IsNullOrWhiteSpace(invite.GlobalIpv6))
            candidates.Add(($"[{invite.GlobalIpv6}]", invite.DirectPort, ConnectionMode.DirectIp));
        if (!string.IsNullOrWhiteSpace(invite.PublicIpv4) && invite.MappedTcpPort > 0)
            candidates.Add((invite.PublicIpv4, invite.MappedTcpPort, ConnectionMode.DirectIp));

        foreach (var (host, port, mode) in candidates.DistinctBy(c => $"{c.Host}:{c.Port}"))
        {
            try
            {
                return (await ConnectDirectTcpAsync(host, port, mode, ct).ConfigureAwait(false), null);
            }
            catch { }
        }

        if (!directOnly)
        {
            return await ConnectViaRelayOrMeshAsync(DeviceIdentity.NormalizeId(invite.StormId), relayUrl, false, ct).ConfigureAwait(false);
        }
        return (null, null);
    }

    private async Task<(ITransportChannel Channel, SconPacket? HostKey)> ConnectViaRelayOrMeshAsync(string targetId, string relayUrl, bool strictCustomRelay, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(relayUrl))
        {
            try
            {
                return (await ConnectViaRelayAsync(targetId, relayUrl, ct).ConfigureAwait(false), null);
            }
            catch when (!strictCustomRelay)
            {
            }
        }
        return await ConnectViaPublicMeshAsync(targetId, ct).ConfigureAwait(false);
    }

    private async Task<(ITransportChannel Channel, SconPacket HostKey)> ConnectViaPublicMeshAsync(string targetId, CancellationToken ct)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        raceCts.CancelAfter(TimeSpan.FromSeconds(12));

        var winnerTcs = new TaskCompletionSource<(MqttPipeTransportChannel Pipe, SconPacket HostKey)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new List<Task>();

        foreach (var brokerUrl in ProtocolConstants.PublicMqttWssBrokers)
        {
            attempts.Add(Task.Run(async () =>
            {
                var mqtt = new MqttWssClient();
                MqttPipeTransportChannel? pipe = null;
                bool won = false;
                try
                {
                    string cid = $"sc_c_{_localStormId}_{Guid.NewGuid().ToString("N")[..6]}";
                    await mqtt.ConnectAsync(brokerUrl, cid, raceCts.Token).ConfigureAwait(false);

                    string token = Guid.NewGuid().ToString("N");
                    pipe = new MqttPipeTransportChannel(mqtt, token, isInitiator: true);
                    await pipe.SubscribeRxAsync(raceCts.Token).ConfigureAwait(false);

                    var inviteJson = JsonSerializer.SerializeToUtf8Bytes(new { Token = token, FromStormId = _localStormId });

                    // Retry publishing invite periodically while awaiting Host's KeyExchange on this broker
                    var rxTask = WaitForKeyExchangeAsync(pipe, raceCts.Token);
                    for (int retry = 0; retry < 6 && !raceCts.IsCancellationRequested; retry++)
                    {
                        await mqtt.PublishAsync($"sc/v1/s/{targetId}", inviteJson, raceCts.Token).ConfigureAwait(false);
                        var finished = await Task.WhenAny(rxTask, Task.Delay(1400, raceCts.Token)).ConfigureAwait(false);
                        if (finished == rxTask) break;
                    }

                    var hostKey = await rxTask.ConfigureAwait(false);
                    if (hostKey is not null && winnerTcs.TrySetResult((pipe, hostKey)))
                    {
                        won = true;
                        raceCts.Cancel();
                    }
                }
                catch { }
                finally
                {
                    if (!won)
                    {
                        if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
                        else await mqtt.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }));
        }

        _ = Task.WhenAll(attempts).ContinueWith(_ =>
        {
            if (!winnerTcs.Task.IsCompleted)
                winnerTcs.TrySetException(new TimeoutException(Services.LocalizationService.T("StatusPeerNoResponse")));
        });

        var result = await winnerTcs.Task.ConfigureAwait(false);
        return (result.Pipe, result.HostKey);
    }

    private static async Task<SconPacket?> WaitForKeyExchangeAsync(ITransportChannel ch, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ch.IsConnected)
        {
            var pkt = await ch.ReceivePacketAsync(ct).ConfigureAwait(false);
            if (pkt is null) return null;
            if (pkt.Type == PacketType.KeyExchange) return pkt;
        }
        return null;
    }

    private static async Task<ITransportChannel> ConnectDirectTcpAsync(string hostSpec, int defaultPort, ConnectionMode mode, CancellationToken ct)
    {
        string host = hostSpec.Trim();
        int port = defaultPort;

        if (host.StartsWith('['))
        {
            int endBracket = host.IndexOf(']');
            if (endBracket > 0)
            {
                string v6 = host[1..endBracket];
                if (endBracket + 2 <= host.Length && host[endBracket + 1] == ':' && int.TryParse(host[(endBracket + 2)..], out int p6))
                    port = p6;
                host = v6;
            }
        }
        else
        {
            int firstColon = host.IndexOf(':');
            int lastColon = host.LastIndexOf(':');
            if (firstColon == lastColon && lastColon > 0 && int.TryParse(host[(lastColon + 1)..], out int parsedPort))
            {
                host = host[..lastColon];
                port = parsedPort;
            }
        }

        IPAddress targetIp;
        if (!IPAddress.TryParse(host, out targetIp!))
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            targetIp = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? addrs.FirstOrDefault()
                    ?? throw new SocketException((int)SocketError.HostNotFound);
        }

        var tcp = new TcpClient(targetIp.AddressFamily);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));
        await tcp.ConnectAsync(targetIp, port, timeoutCts.Token).ConfigureAwait(false);
        return new TcpTransportChannel(tcp, mode);
    }

    private async Task<ITransportChannel> ConnectViaRelayAsync(string targetId, string relayUrl, CancellationToken ct)
    {
        var ctrlWs = new ClientWebSocket();
        await ctrlWs.ConnectAsync(new Uri(relayUrl), ct).ConfigureAwait(false);
        await using var ctrlCh = new WebSocketTransportChannel(ctrlWs, ConnectionMode.WssRelay);

        var reg = new RelayRegisterPayload(_localStormId, Environment.MachineName, "Windows", 1, ProtocolConstants.DefaultDirectPort, null);
        await ctrlCh.SendPacketAsync(PacketType.RelayRegister, FrameCodec.SerializeJson(reg), ct: ct).ConfigureAwait(false);

        var conn = new RelayConnectPayload(_localStormId, targetId);
        await ctrlCh.SendPacketAsync(PacketType.RelayConnect, FrameCodec.SerializeJson(conn), ct: ct).ConfigureAwait(false);

        RelayPairedPayload? paired = null;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        while (!timeoutCts.IsCancellationRequested)
        {
            var pkt = await ctrlCh.ReceivePacketAsync(timeoutCts.Token).ConfigureAwait(false);
            if (pkt is null) break;
            if (pkt.Type == PacketType.RelayError)
                throw new IOException(Encoding.UTF8.GetString(pkt.Payload));
            if (pkt.Type == PacketType.RelayPaired)
            {
                paired = FrameCodec.DeserializeJson<RelayPairedPayload>(pkt.Payload);
                break;
            }
        }

        if (paired is null)
            throw new TimeoutException(Services.LocalizationService.T("StatusRelayTimeout"));

        var baseUri = new Uri(relayUrl);
        var builder = new UriBuilder(baseUri)
        {
            Path = $"/session/{paired.SessionToken}",
            Query = "role=initiator"
        };
        var pipeWs = new ClientWebSocket();
        pipeWs.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await pipeWs.ConnectAsync(builder.Uri, ct).ConfigureAwait(false);
        return new WebSocketTransportChannel(pipeWs, ConnectionMode.WssRelay);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        string disconnectReason = Services.LocalizationService.T("StatusSessionClosed");
        try
        {
            while (_channel is not null && _channel.IsConnected && !ct.IsCancellationRequested)
            {
                var pkt = await _channel.ReceivePacketAsync(ct).ConfigureAwait(false);
                if (pkt is null || pkt.Type == PacketType.Disconnect)
                {
                    if (pkt?.Payload.Length > 0)
                        disconnectReason = Encoding.UTF8.GetString(pkt.Payload);
                    else if (_channel is EncryptedTransportChannel enc && !string.IsNullOrWhiteSpace(enc.InnerCloseReason))
                        disconnectReason = $"{disconnectReason} ({enc.InnerCloseReason})";
                    break;
                }

                Interlocked.Add(ref _bytesRxWindow, pkt.Payload.Length);
                Telemetry.TotalBytesReceived += pkt.Payload.Length;

                switch (pkt.Type)
                {
                    case PacketType.MonitorList:
                        var list = FrameCodec.DeserializeJson<List<MonitorInfo>>(pkt.Payload);
                        if (list is not null)
                        {
                            Monitors = list;
                            MonitorsUpdated?.Invoke(list);
                        }
                        break;

                    case PacketType.VideoFrameFull:
                    case PacketType.VideoFrameTiles:
                        var (w, h, rects) = TileDeltaEncoder.DeserializeVideoUpdate(pkt.Payload);
                        Interlocked.Increment(ref _framesReceivedCount);
                        Interlocked.Increment(ref _framesWindow);
                        if (pkt.Type == PacketType.VideoFrameFull) _lastTileSavings = 0;
                        else
                        {
                            double changedArea = rects.Sum(r => (double)r.Width * r.Height);
                            _lastTileSavings = Math.Round(Math.Clamp(1.0 - changedArea / Math.Max(1.0, (double)w * h), 0, 1) * 100.0, 1);
                        }
                        Telemetry.FrameWidth = w;
                        Telemetry.FrameHeight = h;
                        FrameDimensionsReceived?.Invoke(w, h);
                        ApplyVideoUpdate(w, h, rects);
                        break;

                    case PacketType.AudioFrame:
                        if (_audioEnabled && GrantedPermissions.AllowAudio)
                            _audio.EnqueuePlaybackFrame(pkt.Payload);
                        break;

                    case PacketType.ClipboardData:
                        if (GrantedPermissions.AllowClipboard)
                        {
                            var cb = FrameCodec.DeserializeJson<ClipboardPayload>(pkt.Payload);
                            if (cb is not null) _clipboard.ApplyRemoteClipboard(cb);
                        }
                        break;

                    case PacketType.FileListResp:
                        var fl = FrameCodec.DeserializeJson<FileListResponsePayload>(pkt.Payload);
                        if (fl is not null) FileListReceived?.Invoke(fl);
                        break;

                    case PacketType.FileOperationResp:
                        var op = FrameCodec.DeserializeJson<FileOperationResponsePayload>(pkt.Payload);
                        if (op is not null) FileOperationCompleted?.Invoke(op);
                        break;

                    case PacketType.FileTransferInit:
                        var init = FrameCodec.DeserializeJson<FileTransferInitPayload>(pkt.Payload);
                        if (init is not null && !init.IsUploadToRemote)
                        {
                            var localSavePath = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                "Downloads",
                                "STORM CONNECT",
                                init.FileName);
                            Directory.CreateDirectory(Path.GetDirectoryName(localSavePath)!);
                            var fs = new FileStream(localSavePath, FileMode.Create, FileAccess.Write, FileShare.None);
                            _downloads[init.TransferId] = (fs, localSavePath, init.FileName, init.TotalBytes, 0, DateTime.UtcNow);
                            if (init.TotalBytes == 0)
                            {
                                await fs.DisposeAsync().ConfigureAwait(false);
                                _downloads.TryRemove(init.TransferId, out _);
                                FileTransferProgressed?.Invoke(new FileTransferProgress(init.TransferId, init.FileName, false, 0, 0, 0, true, null));
                            }
                        }
                        break;

                    case PacketType.FileTransferChunk:
                        if (pkt.Payload.Length >= 36)
                        {
                            var tid = Encoding.ASCII.GetString(pkt.Payload, 0, 32).Trim();
                            int dataLen = BitConverter.ToInt32(pkt.Payload, 32);
                            if (_downloads.TryGetValue(tid, out var d))
                            {
                                await d.Stream.WriteAsync(pkt.Payload.AsMemory(36, dataLen), ct).ConfigureAwait(false);
                                long done = d.Done + dataLen;
                                _downloads[tid] = (d.Stream, d.LocalPath, d.FileName, d.Total, done, d.StartUtc);
                                double sec = Math.Max(0.05, (DateTime.UtcNow - d.StartUtc).TotalSeconds);
                                double mbps = (done * 8.0 / 1_000_000.0) / sec;
                                bool completed = done >= d.Total;
                                if (completed)
                                {
                                    await d.Stream.DisposeAsync().ConfigureAwait(false);
                                    _downloads.TryRemove(tid, out _);
                                }
                                FileTransferProgressed?.Invoke(new FileTransferProgress(tid, d.FileName, false, done, d.Total, Math.Round(mbps, 2), completed, null));
                            }
                        }
                        break;

                    case PacketType.FileTransferAck:
                        var ack = FrameCodec.DeserializeJson<FileTransferAckPayload>(pkt.Payload);
                        if (ack is not null)
                        {
                            FileTransferProgressed?.Invoke(new FileTransferProgress(
                                ack.TransferId, "", true, ack.BytesReceived, ack.BytesReceived, 0, ack.Success, ack.Success ? null : ack.Message));
                        }
                        break;

                    case PacketType.ChatMessage:
                        var chat = FrameCodec.DeserializeJson<ChatMessagePayload>(pkt.Payload);
                        if (chat is not null) ChatReceived?.Invoke(chat);
                        break;

                    case PacketType.Pong:
                        if (pkt.Payload.Length >= 8)
                        {
                            long sentTicks = BitConverter.ToInt64(pkt.Payload, 0);
                            _lastRttMs = (int)Math.Max(1, (DateTime.UtcNow.Ticks - sentTicks) / TimeSpan.TicksPerMillisecond);
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            disconnectReason = ex.Message;
        }
        finally
        {
            foreach (var d in _downloads.Values)
            {
                try { await d.Stream.DisposeAsync(); } catch { }
            }
            _downloads.Clear();
            Disconnected?.Invoke(disconnectReason);
        }
    }

    public static WriteableBitmap DecodeSlicesIntoBitmap(WriteableBitmap? existing, int frameW, int frameH, List<VideoTileRect> rects)
    {
        var decoded = new List<(int X, int Y, int W, int H, int Stride, byte[] Bgra)>(rects.Count);
        foreach (var r in rects)
        {
            using var ms = new MemoryStream(r.JpegBytes, writable: false);
            var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource src = decoder.Frames[0];
            if (src.Format != PixelFormats.Bgra32)
            {
                src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            }
            int w = src.PixelWidth;
            int h = src.PixelHeight;
            int stride = w * 4;
            var px = new byte[stride * h];
            src.CopyPixels(px, stride, 0);
            decoded.Add((r.X, r.Y, w, h, stride, px));
        }

        if (existing is null || existing.PixelWidth != frameW || existing.PixelHeight != frameH)
        {
            existing = new WriteableBitmap(frameW, frameH, 96, 96, PixelFormats.Bgra32, null);
        }

        foreach (var d in decoded)
        {
            int copyW = Math.Min(d.W, frameW - d.X);
            int copyH = Math.Min(d.H, frameH - d.Y);
            if (copyW <= 0 || copyH <= 0 || d.X < 0 || d.Y < 0) continue;
            existing.WritePixels(new Int32Rect(0, 0, copyW, copyH), d.Bgra, d.Stride, d.X, d.Y);
        }
        return existing;
    }

    private void ApplyVideoUpdate(int frameW, int frameH, List<VideoTileRect> rects)
    {
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp is null) return;

        try
        {
            // Decode JPEG slices via native WPF WIC JpegBitmapDecoder off the UI thread
            var decoded = new List<(int X, int Y, int W, int H, int Stride, byte[] Bgra)>(rects.Count);
            foreach (var r in rects)
            {
                using var ms = new MemoryStream(r.JpegBytes, writable: false);
                var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                BitmapSource src = decoder.Frames[0];
                if (src.Format != PixelFormats.Bgra32)
                {
                    src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                }
                int w = src.PixelWidth;
                int h = src.PixelHeight;
                int stride = w * 4;
                var px = new byte[stride * h];
                src.CopyPixels(px, stride, 0);
                decoded.Add((r.X, r.Y, w, h, stride, px));
            }

            disp.BeginInvoke(() =>
            {
                try
                {
                    lock (_bmpLock)
                    {
                        if (_bitmap is null || _bitmap.PixelWidth != frameW || _bitmap.PixelHeight != frameH)
                        {
                            _bitmap = new WriteableBitmap(frameW, frameH, 96, 96, PixelFormats.Bgra32, null);
                        }

                        foreach (var d in decoded)
                        {
                            int copyW = Math.Min(d.W, frameW - d.X);
                            int copyH = Math.Min(d.H, frameH - d.Y);
                            if (copyW <= 0 || copyH <= 0 || d.X < 0 || d.Y < 0) continue;
                            _bitmap.WritePixels(new Int32Rect(0, 0, copyW, copyH), d.Bgra, d.Stride, d.X, d.Y);
                        }
                    }
                    FrameUpdated?.Invoke(_bitmap);
                }
                catch (Exception ex)
                {
                    Services.SessionLogger.Log("CLIENT", $"UI WritePixels error: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Services.SessionLogger.Log("CLIENT", $"JPEG WIC decode error: {ex.Message}");
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && IsConnected)
        {
            try
            {
                await _channel!.SendPacketAsync(PacketType.Ping, BitConverter.GetBytes(DateTime.UtcNow.Ticks), ct: ct).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                double sec = Math.Max(0.25, (now - _telemetryWindowStart).TotalSeconds);
                long rx = Interlocked.Exchange(ref _bytesRxWindow, 0);
                int frames = Interlocked.Exchange(ref _framesWindow, 0);
                _telemetryWindowStart = now;

                _lastFps = Math.Round(frames / sec, 1);
                _lastMbps = Math.Round((rx * 8.0 / 1_000_000.0) / sec, 2);

                Telemetry.Fps = _lastFps;
                Telemetry.BandwidthMbps = _lastMbps;
                Telemetry.RttMs = _lastRttMs;
                Telemetry.TileSavingsPercent = _lastTileSavings;
                Telemetry.ActiveMonitorIndex = _activeMonitorIndex;
                TelemetryUpdated?.Invoke(Telemetry);
            }
            catch { }

            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task SelectMonitorAsync(int monitorIndex, int jpegQuality = 72)
    {
        _activeMonitorIndex = monitorIndex;
        if (_channel is null) return;
        var buf = new byte[8];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), monitorIndex);
        BitConverter.TryWriteBytes(buf.AsSpan(4, 4), jpegQuality);
        await _channel.SendPacketAsync(PacketType.MonitorSelect, buf).ConfigureAwait(false);
    }

    public Task SendMouseAsync(MouseEventData ev)
    {
        if (_channel is null || !GrantedPermissions.AllowControl) return Task.CompletedTask;
        var buf = new byte[20];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 8), ev.NormX);
        BitConverter.TryWriteBytes(buf.AsSpan(8, 8), ev.NormY);
        buf[16] = ev.Button;
        buf[17] = ev.Action;
        BitConverter.TryWriteBytes(buf.AsSpan(18, 2), ev.WheelDelta);
        return _channel.SendPacketAsync(PacketType.InputMouse, buf);
    }

    public Task SendKeyAsync(KeyEventData ev)
    {
        if (_channel is null || !GrantedPermissions.AllowControl) return Task.CompletedTask;
        var buf = new byte[8];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 2), ev.VirtualKey);
        BitConverter.TryWriteBytes(buf.AsSpan(2, 2), ev.ScanCode);
        buf[4] = ev.IsKeyUp ? (byte)1 : (byte)0;
        buf[5] = ev.IsExtended ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(buf.AsSpan(6, 2), (ushort)(ev.UnicodeChar ?? '\0'));
        return _channel.SendPacketAsync(PacketType.InputKeyboard, buf);
    }

    public Task SendSystemActionAsync(SystemActionKind kind)
    {
        if (_channel is null) return Task.CompletedTask;
        return _channel.SendPacketAsync(PacketType.SessionAction, new byte[] { (byte)kind });
    }

    public Task RequestFileListAsync(string path)
    {
        if (_channel is null) return Task.CompletedTask;
        return _channel.SendPacketAsync(PacketType.FileListReq, Encoding.UTF8.GetBytes(path));
    }

    public Task RequestFileOperationAsync(FileOperationRequestPayload op)
    {
        if (_channel is null) return Task.CompletedTask;
        return _channel.SendPacketAsync(PacketType.FileOperationReq, FrameCodec.SerializeJson(op));
    }

    public Task RequestFileDownloadAsync(string remotePath, string fileName)
    {
        if (_channel is null) return Task.CompletedTask;
        var tid = Guid.NewGuid().ToString("N");
        var init = new FileTransferInitPayload(tid, IsUploadToRemote: false, remotePath, fileName, 0, "");
        return _channel.SendPacketAsync(PacketType.FileTransferInit, FrameCodec.SerializeJson(init));
    }

    public async Task UploadFileAsync(string localFilePath, string remoteDestinationDir, CancellationToken ct = default)
    {
        if (_channel is null || !File.Exists(localFilePath)) return;
        var fi = new FileInfo(localFilePath);
        var tid = Guid.NewGuid().ToString("N");
        string remotePath = string.IsNullOrWhiteSpace(remoteDestinationDir)
            ? fi.Name
            : Path.Combine(remoteDestinationDir, fi.Name);

        string sha;
        await using (var hfs = File.OpenRead(localFilePath))
            sha = Convert.ToHexString(await SHA256.HashDataAsync(hfs, ct).ConfigureAwait(false));

        var init = new FileTransferInitPayload(tid, IsUploadToRemote: true, remotePath, fi.Name, fi.Length, sha);
        await _channel.SendPacketAsync(PacketType.FileTransferInit, FrameCodec.SerializeJson(init), ct: ct).ConfigureAwait(false);

        var tidBytes = Encoding.ASCII.GetBytes(tid);
        var buf = new byte[ProtocolConstants.FileChunkBytes + 36];
        Buffer.BlockCopy(tidBytes, 0, buf, 0, 32);

        long sent = 0;
        var start = DateTime.UtcNow;
        await using var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int read;
        while ((read = await fs.ReadAsync(buf.AsMemory(36, ProtocolConstants.FileChunkBytes), ct).ConfigureAwait(false)) > 0)
        {
            BitConverter.TryWriteBytes(buf.AsSpan(32, 4), read);
            await _channel.SendPacketAsync(PacketType.FileTransferChunk, buf.AsMemory(0, 36 + read), ct: ct).ConfigureAwait(false);
            sent += read;
            double sec = Math.Max(0.05, (DateTime.UtcNow - start).TotalSeconds);
            double mbps = (sent * 8.0 / 1_000_000.0) / sec;
            FileTransferProgressed?.Invoke(new FileTransferProgress(tid, fi.Name, true, sent, fi.Length, Math.Round(mbps, 2), sent >= fi.Length, null));
        }
    }

    public Task SendChatAsync(string text)
    {
        if (_channel is null) return Task.CompletedTask;
        var msg = new ChatMessagePayload(Environment.MachineName, text, DateTime.UtcNow);
        return _channel.SendPacketAsync(PacketType.ChatMessage, FrameCodec.SerializeJson(msg));
    }

    public void SetAudioEnabled(bool enabled)
    {
        _audioEnabled = enabled;
        if (enabled && IsConnected && GrantedPermissions.AllowAudio) _audio.StartPlayback();
        else _audio.StopPlayback();
        if (IsConnected)
            _ = SendSystemActionAsync(enabled ? SystemActionKind.EnableAudio : SystemActionKind.DisableAudio);
    }

    private void OnLocalClipboardChanged(ClipboardPayload payload)
    {
        if (_channel is null || !IsConnected || !GrantedPermissions.AllowClipboard) return;
        _ = Task.Run(async () =>
        {
            try { await _channel.SendPacketAsync(PacketType.ClipboardData, FrameCodec.SerializeJson(payload), compress: true).ConfigureAwait(false); } catch { }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _clipboard.LocalClipboardChanged -= OnLocalClipboardChanged;
        _cts?.Cancel();
        _audio.Dispose();
        if (_channel is not null)
        {
            try { await _channel.SendPacketAsync(PacketType.Disconnect, Array.Empty<byte>()).ConfigureAwait(false); } catch { }
            await _channel.DisposeAsync().ConfigureAwait(false);
            _channel = null;
        }
        _cts?.Dispose();
    }
}
