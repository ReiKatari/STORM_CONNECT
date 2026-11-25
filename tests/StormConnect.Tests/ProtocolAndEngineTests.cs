using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using StormConnect.Desktop.Engine;
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
}
