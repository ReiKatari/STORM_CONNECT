using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using StormConnect.Desktop.Engine;
using StormConnect.Desktop.Networking;
using StormConnect.Desktop.Services;
using StormConnect.Protocol;
using Xunit;

namespace StormConnect.Tests;

public sealed class ProtocolAndEngineTests
{
    [Fact]
    public void EcdhAndAesGcm_RoundTrip_And_TamperRejected()
    {
        using var initiator = new CryptoSession();
        using var responder = new CryptoSession();

        initiator.DeriveKeys(responder.LocalPublicKeyUncompressed, isInitiator: true);
        responder.DeriveKeys(initiator.LocalPublicKeyUncompressed, isInitiator: false);

        Assert.True(initiator.IsEstablished);
        Assert.True(responder.IsEstablished);

        var msg = Encoding.UTF8.GetBytes("STORM CONNECT E2E Payload 12345");
        var cipher = initiator.Encrypt(msg);
        var decrypted = responder.Decrypt(cipher);
        Assert.Equal(msg, decrypted);

        // Tampering ciphertext must throw CryptographicException
        var tampered = (byte[])cipher.Clone();
        tampered[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => responder.Decrypt(tampered));

        // Replaying same sequence must be rejected
        Assert.ThrowsAny<CryptographicException>(() => responder.Decrypt(cipher));
    }

    [Fact]
    public void ChallengeProof_Verify_WorksAndRejectsWrongPin()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(32);
        var storedHash = CryptoSession.HashPassword("847291", salt);

        var validProof = CryptoSession.ComputeChallengeProof("847291", salt, nonce);
        var badProof = CryptoSession.ComputeChallengeProof("000000", salt, nonce);

        Assert.True(CryptoSession.VerifyChallengeProof(storedHash, nonce, validProof));
        Assert.False(CryptoSession.VerifyChallengeProof(storedHash, nonce, badProof));
    }

    [Fact]
    public void TileDeltaEncoder_DetectsPartialUpdates_AndSerializesCleanly()
    {
        int w = 256, h = 256;
        var buf1 = new byte[w * h * 4];
        for (int i = 0; i < buf1.Length; i += 4) { buf1[i] = 40; buf1[i + 1] = 20; buf1[i + 2] = 80; buf1[i + 3] = 255; }

        var frame1 = new CapturedFrame { Width = w, Height = h, Stride = w * 4, BgraPixels = buf1 };
        var enc = new TileDeltaEncoder();
        var u1 = enc.Encode(frame1, 75);
        Assert.NotNull(u1);
        Assert.True(u1!.IsFullFrame);

        // Unchanged frame produces null delta (0 bandwidth)
        var uSame = enc.Encode(frame1, 75);
        Assert.Null(uSame);

        // Modify a single 32x32 block inside tile (0,0)
        var buf2 = (byte[])buf1.Clone();
        for (int y = 4; y < 36; y++)
            for (int x = 4; x < 36; x++)
            {
                int off = (y * w + x) * 4;
                buf2[off] = 240; buf2[off + 1] = 200; buf2[off + 2] = 50;
            }
        var frame2 = new CapturedFrame { Width = w, Height = h, Stride = w * 4, BgraPixels = buf2 };
        var u2 = enc.Encode(frame2, 75);
        Assert.NotNull(u2);
        Assert.False(u2!.IsFullFrame);
        Assert.True(u2.TileSavingsPercent > 80.0);

        var wire = TileDeltaEncoder.SerializeVideoUpdate(u2);
        var (dw, dh, rects) = TileDeltaEncoder.DeserializeVideoUpdate(wire);
        Assert.Equal(w, dw);
        Assert.Equal(h, dh);
        Assert.Single(rects);
        Assert.True(rects[0].JpegBytes.Length > 100);
    }

    [Fact]
    public async Task LoopbackEncryptedTcpChannel_ExchangesPackets()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = listener.AcceptTcpClientAsync();
        using var clientTcp = new TcpClient();
        await clientTcp.ConnectAsync(IPAddress.Loopback, port);
        using var serverTcp = await acceptTask;
        listener.Stop();

        await using var chClient = new TcpTransportChannel(clientTcp, ConnectionMode.LanDirect);
        await using var chServer = new TcpTransportChannel(serverTcp, ConnectionMode.LanDirect);

        using var cCrypto = new CryptoSession();
        using var sCrypto = new CryptoSession();
        await chServer.SendPacketAsync(PacketType.KeyExchange, sCrypto.LocalPublicKeyUncompressed);
        var sk = await chClient.ReceivePacketAsync();
        await chClient.SendPacketAsync(PacketType.KeyExchange, cCrypto.LocalPublicKeyUncompressed);
        var ck = await chServer.ReceivePacketAsync();

        cCrypto.DeriveKeys(sk!.Payload, true);
        sCrypto.DeriveKeys(ck!.Payload, false);

        await using var encClient = new EncryptedTransportChannel(chClient, cCrypto);
        await using var encServer = new EncryptedTransportChannel(chServer, sCrypto);

        var hello = new HelloPayload("123456789", "TEST-PC", "Windows", "1.0.0", 2, true);
        await encServer.SendPacketAsync(PacketType.Hello, FrameCodec.SerializeJson(hello), compress: true);
        var rx = await encClient.ReceivePacketAsync();
        Assert.NotNull(rx);
        Assert.Equal(PacketType.Hello, rx!.Type);
        var decoded = FrameCodec.DeserializeJson<HelloPayload>(rx.Payload);
        Assert.Equal("TEST-PC", decoded?.HostName);
    }

    [Fact]
    public void Localization_All11Languages_CompleteAndZeroEmDash()
    {
        Assert.Equal(11, LocalizationService.Languages.Count);
        var enKeys = LocalizationService.GetAllStringsForLanguage("en").Select(k => k.Key).ToList();
        foreach (var lang in LocalizationService.Languages)
        {
            var dict = LocalizationService.GetAllStringsForLanguage(lang.Code).ToDictionary(k => k.Key, k => k.Value);
            foreach (var key in enKeys)
            {
                Assert.True(dict.ContainsKey(key), $"Missing key {key} in {lang.Code}");
                Assert.False(string.IsNullOrWhiteSpace(dict[key]), $"Empty value for {key} in {lang.Code}");
                Assert.DoesNotContain("\u2014", dict[key]);
                Assert.DoesNotContain("\u2013", dict[key]);
            }
        }
    }

    [Fact]
    public void WakeOnLan_MagicPacket_HasValidStructure()
    {
        var pkt = WakeOnLan.BuildMagicPacket("AA:BB:CC:DD:EE:FF");
        Assert.Equal(102, pkt.Length);
        for (int i = 0; i < 6; i++) Assert.Equal(0xFF, pkt[i]);
        Assert.Equal(0xAA, pkt[6]);
        Assert.Equal(0xFF, pkt[11]);
    }

    [Fact]
    public void InviteCode_EncodeDecode_RoundTrip()
    {
        var src = new InviteCodePayload(
            "123456789",
            "WORKSTATION",
            7440,
            "192.168.1.50",
            "100.64.0.12",
            "203.0.113.45",
            7440,
            "203.0.113.45:51234",
            "2001:db8::1");

        var code = InviteCode.Encode(src);
        Assert.StartsWith("SC1-", code);
        Assert.True(InviteCode.TryDecode(code, out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(src.StormId, decoded!.StormId);
        Assert.Equal(src.LanIp, decoded.LanIp);
        Assert.Equal(src.VpnIp, decoded.VpnIp);
        Assert.Equal(src.PublicIpv4, decoded.PublicIpv4);
        Assert.Equal(src.GlobalIpv6, decoded.GlobalIpv6);
    }

    [Fact]
    public void StunClient_ParsesRfc5389XorMappedAddress()
    {
        var txId = new byte[12] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var req = StunClient.BuildBindingRequest(txId);
        Assert.Equal(20, req.Length);

        // Construct valid Binding Success Response with XOR-MAPPED-ADDRESS for 198.51.100.77:54321
        var resp = new byte[32];
        resp[0] = 0x01; resp[1] = 0x01; // Binding Success
        resp[2] = 0x00; resp[3] = 0x0C; // 12 bytes attr
        resp[4] = 0x21; resp[5] = 0x12; resp[6] = 0xA4; resp[7] = 0x42; // Cookie
        Buffer.BlockCopy(txId, 0, resp, 8, 12);

        resp[20] = 0x00; resp[21] = 0x20; // XOR-MAPPED-ADDRESS
        resp[22] = 0x00; resp[23] = 0x08; // Len 8
        resp[24] = 0x00; resp[25] = 0x01; // Family IPv4
        ushort xPort = (ushort)(54321 ^ 0x2112);
        resp[26] = (byte)(xPort >> 8); resp[27] = (byte)(xPort & 0xFF);
        resp[28] = (byte)(198 ^ 0x21);
        resp[29] = (byte)(51 ^ 0x12);
        resp[30] = (byte)(100 ^ 0xA4);
        resp[31] = (byte)(77 ^ 0x42);

        var ep = StunClient.ParseBindingResponse(resp, txId);
        Assert.NotNull(ep);
        Assert.Equal("198.51.100.77", ep!.Address.ToString());
        Assert.Equal(54321, ep.Port);
    }

    [Fact]
    public void SelectorOptions_ToString_NeverReturnsRecordTypeName()
    {
        foreach (var l in LocalizationService.Languages)
            Assert.Equal(l.DisplayName, l.ToString());
        foreach (var t in ThemeService.Themes)
            Assert.Equal(t.Name, t.ToString());
        foreach (var a in ThemeService.Accents)
            Assert.Equal(a.Name, a.ToString());
        foreach (var f in ThemeService.Fonts)
            Assert.Equal(f.DisplayName, f.ToString());
    }

    [Fact]
    public async Task EndToEnd_DirectTcp_AuthMonitorListFileListAndKeepAlive()
    {
        const string hostId = "900111222";
        const string clientId = "900333444";
        const string pin = "654321";

        using var hostClip = new ClipboardWatcher();
        using var clientClip = new ClipboardWatcher();
        await using var host = new HostServer(
            hostId,
            () => pin,
            () => (null, null),
            () => new SessionPermissions(),
            () => true,
            _ => Task.FromResult(new ConsentDecisionPayload(false, new SessionPermissions())),
            hostClip);

        host.Start(0, "", enableRelay: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (host.ListeningPort == 0 && sw.ElapsedMilliseconds < 3000) await Task.Delay(25);
        Assert.True(host.ListeningPort > 0);

        await using var client = new ClientSession(clientId, clientClip);
        var monsTcs = new TaskCompletionSource<IReadOnlyList<MonitorInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var filesTcs = new TaskCompletionSource<FileListResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MonitorsUpdated += m => monsTcs.TrySetResult(m);
        client.FileListReceived += f => filesTcs.TrySetResult(f);

        await client.ConnectAsync($"127.0.0.1:{host.ListeningPort}", "", Array.Empty<DiscoveredDevice>(), (_, _) => Task.FromResult<string?>(pin));
        Assert.True(client.IsConnected);

        var mons = await monsTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(mons);

        // Wait > 1.2s so PingLoop executes and proves CryptoSession remained alive after ConnectAsync
        await Task.Delay(1300);
        Assert.True(client.IsConnected);

        await client.RequestFileListAsync("");
        var files = await filesTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(files.Entries);
    }

    [Fact]
    public async Task EndToEnd_InviteCode_ServerlessConnect()
    {
        const string hostId = "811222333";
        const string clientId = "811444555";
        const string pin = "112233";

        using var hostClip = new ClipboardWatcher();
        using var clientClip = new ClipboardWatcher();
        await using var host = new HostServer(
            hostId,
            () => pin,
            () => (null, null),
            () => new SessionPermissions(),
            () => true,
            _ => Task.FromResult(new ConsentDecisionPayload(false, new SessionPermissions())),
            hostClip);

        host.Start(0, "", enableRelay: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (host.ListeningPort == 0 && sw.ElapsedMilliseconds < 3000) await Task.Delay(25);

        string code = InviteCode.Encode(new InviteCodePayload(hostId, "HOST", host.ListeningPort, "127.0.0.1", null, null, 0, null, null));
        await using var client = new ClientSession(clientId, clientClip);
        await client.ConnectAsync(code, "", Array.Empty<DiscoveredDevice>(), (_, _) => Task.FromResult<string?>(pin), preferredMethodIndex: 2);
        Assert.True(client.IsConnected);
        Assert.Equal(ConnectionMode.LanDirect, client.Telemetry.Mode);
    }

    [Fact]
    public async Task EndToEnd_PublicMqttWss_9DigitStormId_LiveConnect()
    {
        string hostId = RandomNumberGenerator.GetInt32(700_000_000, 799_999_999).ToString();
        string clientId = RandomNumberGenerator.GetInt32(600_000_000, 699_999_999).ToString();
        const string pin = "778899";

        using var hostClip = new ClipboardWatcher();
        using var clientClip = new ClipboardWatcher();
        await using var host = new HostServer(
            hostId,
            () => pin,
            () => (null, null),
            () => new SessionPermissions(),
            () => true,
            _ => Task.FromResult(new ConsentDecisionPayload(false, new SessionPermissions())),
            hostClip);

        host.Start(0, "", enableRelay: true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!host.IsRelayConnected && sw.ElapsedMilliseconds < 10000) await Task.Delay(100);
        Assert.True(host.IsRelayConnected, "Host failed to join Public MQTT-WSS Mesh");

        await using var client = new ClientSession(clientId, clientClip);
        var monsTcs = new TaskCompletionSource<IReadOnlyList<MonitorInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var filesTcs = new TaskCompletionSource<FileListResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MonitorsUpdated += m => monsTcs.TrySetResult(m);
        client.FileListReceived += f => filesTcs.TrySetResult(f);

        await client.ConnectAsync(hostId, "", Array.Empty<DiscoveredDevice>(), (_, _) => Task.FromResult<string?>(pin), preferredMethodIndex: 3);
        Assert.True(client.IsConnected);
        Assert.Equal(ConnectionMode.WssRelay, client.Telemetry.Mode);

        var mons = await monsTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(mons);

        await client.RequestFileListAsync("");
        var files = await filesTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(files.Entries);
    }

    [Fact]
    public async Task EndToEnd_PublicMqttWss_MultiBroker_AndKeepAlive()
    {
        string hostId = RandomNumberGenerator.GetInt32(820_000_000, 899_999_999).ToString();
        string clientId = RandomNumberGenerator.GetInt32(720_000_000, 799_999_999).ToString();
        const string pin = "445566";

        using var hostClip = new ClipboardWatcher();
        using var clientClip = new ClipboardWatcher();
        await using var host = new HostServer(
            hostId,
            () => pin,
            () => (null, null),
            () => new SessionPermissions(),
            () => true,
            _ => Task.FromResult(new ConsentDecisionPayload(false, new SessionPermissions())),
            hostClip);

        host.Start(0, "", enableRelay: true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (host.ConnectedBrokerCount < 2 && sw.ElapsedMilliseconds < 10000) await Task.Delay(100);
        Assert.True(host.ConnectedBrokerCount >= 2, $"Expected >=2 mesh brokers, got {host.ConnectedBrokerCount}");

        await using var client = new ClientSession(clientId, clientClip);
        var monsTcs = new TaskCompletionSource<IReadOnlyList<MonitorInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MonitorsUpdated += m => monsTcs.TrySetResult(m);

        await client.ConnectAsync(hostId, "", Array.Empty<DiscoveredDevice>(), (_, _) => Task.FromResult<string?>(pin), preferredMethodIndex: 3);
        Assert.True(client.IsConnected);

        var mons = await monsTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(mons);
        await Task.Delay(2200);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task EndToEnd_PublicMqttWss_StreamsRealVideoFramesAndKeepsAlive()
    {
        string hostId = RandomNumberGenerator.GetInt32(830_000_000, 899_999_999).ToString();
        string clientId = RandomNumberGenerator.GetInt32(730_000_000, 799_999_999).ToString();
        const string pin = "991122";

        using var hostClip = new ClipboardWatcher();
        using var clientClip = new ClipboardWatcher();
        await using var host = new HostServer(
            hostId,
            () => pin,
            () => (null, null),
            () => new SessionPermissions(),
            () => true,
            _ => Task.FromResult(new ConsentDecisionPayload(false, new SessionPermissions())),
            hostClip);

        var diagLogs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        host.AuditLogEvent += m => diagLogs.Enqueue($"[HOST] {m}");

        host.Start(0, "", enableRelay: true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!host.IsRelayConnected && sw.ElapsedMilliseconds < 10000) await Task.Delay(100);
        Assert.True(host.IsRelayConnected);

        await using var client = new ClientSession(clientId, clientClip);
        client.StatusChanged += m => diagLogs.Enqueue($"[CL-ST] {m}");
        client.Disconnected += r => diagLogs.Enqueue($"[CL-DC] {r}");
        var frameTcs = new TaskCompletionSource<(int W, int H)>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameDimensionsReceived += (w, h) => frameTcs.TrySetResult((w, h));

        await client.ConnectAsync(hostId, "", Array.Empty<DiscoveredDevice>(), (_, _) => Task.FromResult<string?>(pin), preferredMethodIndex: 3);
        Assert.True(client.IsConnected);

        var (fw, fh) = await frameTcs.Task.WaitAsync(TimeSpan.FromSeconds(12))
            .ContinueWith(t => t.IsCompletedSuccessfully
                ? t.Result
                : throw new TimeoutException($"Frame timeout! Logs: {string.Join(" | ", diagLogs)} ; RxBytes={client.Telemetry.TotalBytesReceived} ; Connected={client.IsConnected}"));
        Assert.True(fw > 0 && fh > 0, $"Invalid frame size {fw}x{fh}");
        Assert.True(client.FramesReceivedCount >= 1);

        // Ensure connection stays stable after streaming video frames & concurrent file listing
        var filesTcs = new TaskCompletionSource<FileListResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FileListReceived += f => filesTcs.TrySetResult(f);
        await client.RequestFileListAsync("");
        var files = await filesTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(files.Entries);
        await Task.Delay(3200);
        Assert.True(client.IsConnected);
        Assert.DoesNotContain(ProtocolConstants.PublicMqttWssBrokers, b => b.Contains("emqx", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DeviceIdentity_FormatInputLive_GroupsEveryThreeDigitsWithoutTouchingIpsOrCodes()
    {
        Assert.Equal("596", DeviceIdentity.FormatInputLive("596"));
        Assert.Equal("596 6", DeviceIdentity.FormatInputLive("5966"));
        Assert.Equal("596 660", DeviceIdentity.FormatInputLive("596660"));
        Assert.Equal("596 660 745", DeviceIdentity.FormatInputLive("596660745"));
        Assert.Equal("596 660 745", DeviceIdentity.FormatInputLive("596 660745"));
        Assert.Equal("192.168.1.50:7440", DeviceIdentity.FormatInputLive("192.168.1.50:7440"));
        Assert.Equal("SC1-abc123", DeviceIdentity.FormatInputLive("SC1-abc123"));
        Assert.Equal("1234567890", DeviceIdentity.FormatInputLive("1234567890"));
    }

    [Fact]
    public void CryptoSession_SlidingWindow_AcceptsMinorOutOfOrderAndRejectsReplay()
    {
        using var tx = new CryptoSession();
        using var rx = new CryptoSession();
        tx.DeriveKeys(rx.LocalPublicKeyUncompressed, isInitiator: true);
        rx.DeriveKeys(tx.LocalPublicKeyUncompressed, isInitiator: false);

        var c1 = tx.Encrypt(Encoding.UTF8.GetBytes("pkt-1"));
        var c2 = tx.Encrypt(Encoding.UTF8.GetBytes("pkt-2"));
        var c3 = tx.Encrypt(Encoding.UTF8.GetBytes("pkt-3"));

        Assert.Equal("pkt-1", Encoding.UTF8.GetString(rx.Decrypt(c1)));
        Assert.Equal("pkt-3", Encoding.UTF8.GetString(rx.Decrypt(c3)));
        Assert.Equal("pkt-2", Encoding.UTF8.GetString(rx.Decrypt(c2)));

        // Duplicate replays must be rejected
        Assert.ThrowsAny<CryptographicException>(() => rx.Decrypt(c2));
        Assert.ThrowsAny<CryptographicException>(() => rx.Decrypt(c3));
    }

    [Fact]
    public void TileDeltaEncoder_DownscalesOversizedFrameForMesh()
    {
        int w = 1920, h = 1080;
        var buf = new byte[w * h * 4];
        for (int i = 0; i < buf.Length; i += 4) { buf[i] = 30; buf[i + 1] = 60; buf[i + 2] = 120; buf[i + 3] = 255; }
        var frame = new CapturedFrame { Width = w, Height = h, Stride = w * 4, BgraPixels = buf };
        var enc = new TileDeltaEncoder();
        var update = enc.Encode(frame, 55, forceFullFrame: true, maxWidth: 1280);
        Assert.NotNull(update);
        Assert.Equal(1280, update!.FrameWidth);
        Assert.Equal(720, update.FrameHeight);
        Assert.True(update.Rects[0].JpegBytes.Length < 120 * 1024);
    }

    [Fact]
    public void SettingsStore_SavedConnectionAndUnattendedToggle_Works()
    {
        var store = new SettingsStore();
        var enc = SettingsStore.ProtectSecret("StormSecret123!");
        Assert.NotNull(enc);
        Assert.Equal("StormSecret123!", SettingsStore.UnprotectSecret(enc));

        store.SetUnattendedPassword("HostPass99");
        Assert.True(store.Current.EnableUnattendedPassword);
        var (h1, s1) = store.GetUnattendedCredentials();
        Assert.NotNull(h1);
        Assert.NotNull(s1);

        store.Current.EnableUnattendedPassword = false;
        var (h2, s2) = store.GetUnattendedCredentials();
        Assert.Null(h2);
        Assert.Null(s2);

        store.SetUnattendedPassword(null);
    }

    [Fact]
    public void ThemeService_Fonts_TenDistinctFamiliesAndNoMissingNames()
    {
        Assert.Equal(10, ThemeService.Fonts.Count);
        var distinct = ThemeService.Fonts.Select(f => f.WpfFamily).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(10, distinct);
    }

    [Fact]
    public void TileDeltaEncoder_SizeBudget_EnforcesAtomicWssLimit()
    {
        int w = 3840, h = 2160;
        var px = new byte[w * h * 4];
        var rng = new Random(123);
        rng.NextBytes(px);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        var frame = new CapturedFrame { Width = w, Height = h, Stride = w * 4, BgraPixels = px };

        var enc = new TileDeltaEncoder();
        var upd = enc.EncodeWithSizeBudget(frame, 55, forceFullFrame: true, maxWidth: 1280, maxPacketBytes: 54 * 1024);
        Assert.NotNull(upd);
        var wire = TileDeltaEncoder.SerializeVideoUpdate(upd!);
        Assert.True(wire.Length <= 54 * 1024, $"Expected <= 54KB atomic frame, got {wire.Length} bytes");
    }

    [Fact]
    public void WpfVideoDecoder_DecodesFullAndDeltaTileIntoWriteableBitmap()
    {
        Exception? staEx = null;
        bool hasNonZero = false;
        var t = new Thread(() =>
        {
            try
            {
                int w = 256, h = 256;
                var px1 = new byte[w * h * 4];
                for (int i = 0; i < px1.Length; i += 4) { px1[i] = 20; px1[i + 1] = 120; px1[i + 2] = 220; px1[i + 3] = 255; }
                var enc = new TileDeltaEncoder();
                var u1 = enc.Encode(new CapturedFrame { Width = w, Height = h, Stride = w * 4, BgraPixels = px1 }, 80, true)!;
                var wb = ClientSession.DecodeSlicesIntoBitmap(null, u1.FrameWidth, u1.FrameHeight, u1.Rects);

                var px2 = (byte[])px1.Clone();
                for (int y = 0; y < 64; y++)
                    for (int x = 64; x < 128; x++)
                    {
                        int off = (y * w + x) * 4;
                        px2[off] = 250; px2[off + 1] = 240; px2[off + 2] = 10;
                    }
                var u2 = enc.Encode(new CapturedFrame { Width = w, Height = h, Stride = w * 4, BgraPixels = px2 }, 80, false)!;
                wb = ClientSession.DecodeSlicesIntoBitmap(wb, u2.FrameWidth, u2.FrameHeight, u2.Rects);

                var outBuf = new byte[w * h * 4];
                wb.CopyPixels(outBuf, w * 4, 0);
                int sampleOff = (10 * w + 74) * 4;
                hasNonZero = outBuf[sampleOff] > 200 && outBuf[sampleOff + 1] > 200;
            }
            catch (Exception ex) { staEx = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(staEx);
        Assert.True(hasNonZero);
    }

    [Fact]
    public async Task EndToEnd_DualAuth_HostConsentWinsWhileClientPromptOpen()
    {
        const string hostId = "911111222";
        const string clientId = "911333444";
        const string pin = "654321";

        using var hostClip = new ClipboardWatcher();
        using var clientClip = new ClipboardWatcher();
        var hostPromptShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostApproveTcs = new TaskCompletionSource<ConsentDecisionPayload>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = new HostServer(
            hostId,
            () => pin,
            () => (null, null),
            () => new SessionPermissions(),
            () => true,
            async (req, ct) =>
            {
                hostPromptShown.TrySetResult();
                using var _ = ct.Register(() => hostApproveTcs.TrySetResult(new ConsentDecisionPayload(false, req.ProposedPermissions)));
                return await hostApproveTcs.Task.ConfigureAwait(false);
            },
            hostClip);

        host.Start(0, "", enableRelay: false);
        while (host.ListeningPort == 0) await Task.Delay(20);

        await using var client = new ClientSession(clientId, clientClip);
        var clientPromptShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientClosedByHost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var connTask = client.ConnectDualAuthAsync(
            $"127.0.0.1:{host.ListeningPort}",
            "",
            Array.Empty<DiscoveredDevice>(),
            savedSecret: null,
            ctx =>
            {
                clientPromptShown.TrySetResult();
                ctx.CompletedToken.Register(() => clientClosedByHost.TrySetResult());
                return Task.CompletedTask;
            });

        await hostPromptShown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clientPromptShown.Task.WaitAsync(TimeSpan.FromSeconds(5));

        hostApproveTcs.TrySetResult(new ConsentDecisionPayload(true, new SessionPermissions()));
        await connTask.WaitAsync(TimeSpan.FromSeconds(5));
        await clientClosedByHost.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task EndToEnd_DualAuth_ClientPinWinsAndCancelsHostConsent()
    {
        const string hostId = "922111222";
        const string clientId = "922333444";
        const string pin = "789012";

        using var hostClip = new ClipboardWatcher();
        using var clientClip = new ClipboardWatcher();
        var hostPromptShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostPromptCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = new HostServer(
            hostId,
            () => pin,
            () => (null, null),
            () => new SessionPermissions(),
            () => true,
            async (req, ct) =>
            {
                hostPromptShown.TrySetResult();
                var tcs = new TaskCompletionSource<ConsentDecisionPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var _ = ct.Register(() =>
                {
                    hostPromptCancelled.TrySetResult();
                    tcs.TrySetResult(new ConsentDecisionPayload(false, req.ProposedPermissions));
                });
                return await tcs.Task.ConfigureAwait(false);
            },
            hostClip);

        host.Start(0, "", enableRelay: false);
        while (host.ListeningPort == 0) await Task.Delay(20);

        await using var client = new ClientSession(clientId, clientClip);
        DualAuthContext? capturedCtx = null;
        var clientPromptShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var connTask = client.ConnectDualAuthAsync(
            $"127.0.0.1:{host.ListeningPort}",
            "",
            Array.Empty<DiscoveredDevice>(),
            savedSecret: null,
            ctx =>
            {
                capturedCtx = ctx;
                clientPromptShown.TrySetResult();
                return Task.CompletedTask;
            });

        await hostPromptShown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clientPromptShown.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Client submits wrong PIN first -> ErrorReceived, then submits correct PIN -> Host Consent cancels!
        var errTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        capturedCtx!.ErrorReceived += e => errTcs.TrySetResult(e);
        await capturedCtx.SubmitSecretAsync("000000");
        await errTcs.Task.WaitAsync(TimeSpan.FromSeconds(4));

        await capturedCtx.SubmitSecretAsync(pin);
        await connTask.WaitAsync(TimeSpan.FromSeconds(5));
        await hostPromptCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task EndToEnd_UnattendedPassword_InstantSilentLoginWithoutConsent()
    {
        const string hostId = "933111222";
        const string clientId = "933333444";
        const string unattPwd = "MyPermanentSecret!";
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = CryptoSession.HashPassword(unattPwd, salt);

        using var hostClip = new ClipboardWatcher();
        using var clientClip = new ClipboardWatcher();
        bool consentTriggered = false;
        bool clientModalTriggered = false;

        await using var host = new HostServer(
            hostId,
            () => "111111",
            () => (hash, salt),
            () => new SessionPermissions(),
            () => true,
            (_, _) =>
            {
                consentTriggered = true;
                return Task.FromResult(new ConsentDecisionPayload(false, new SessionPermissions()));
            },
            hostClip);

        host.Start(0, "", enableRelay: false);
        while (host.ListeningPort == 0) await Task.Delay(20);

        await using var client = new ClientSession(clientId, clientClip);
        await client.ConnectDualAuthAsync(
            $"127.0.0.1:{host.ListeningPort}",
            "",
            Array.Empty<DiscoveredDevice>(),
            savedSecret: unattPwd,
            _ => { clientModalTriggered = true; return Task.CompletedTask; });

        Assert.True(client.IsConnected);
        Assert.False(consentTriggered);
        Assert.False(clientModalTriggered);
    }
}
