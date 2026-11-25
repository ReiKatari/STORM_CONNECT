using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace StormConnect.Protocol;

public sealed record SconPacket(PacketType Type, byte Flags, byte[] Payload);

public interface ITransportChannel : IAsyncDisposable
{
    ConnectionMode Mode { get; }
    bool IsConnected { get; }
    Task SendPacketAsync(PacketType type, ReadOnlyMemory<byte> payload, bool compress = false, CancellationToken ct = default);
    Task<SconPacket?> ReceivePacketAsync(CancellationToken ct = default);
}

public static class FrameCodec
{
    public const int HeaderSize = 10; // [4B Magic][1B Version][1B Type][1B Flags][4B PayloadLen] -> wait: 4+1+1+1+4 = 11 bytes!
    public const int WireHeaderBytes = 11;
    public const byte FlagCompressed = 0x01;
    public const byte FlagEncrypted = 0x02;

    public static byte[] EncodePlainPacket(PacketType type, ReadOnlySpan<byte> payload, byte flags = 0)
    {
        var buffer = new byte[WireHeaderBytes + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), ProtocolConstants.Magic);
        buffer[4] = ProtocolConstants.ProtocolVersion;
        buffer[5] = (byte)type;
        buffer[6] = flags;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(7, 4), payload.Length);
        payload.CopyTo(buffer.AsSpan(WireHeaderBytes));
        return buffer;
    }

    public static byte[] Compress(ReadOnlySpan<byte> raw)
    {
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(raw);
        }
        return ms.ToArray();
    }

    public static byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    public static byte[] SerializeJson<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value);
    }

    public static T? DeserializeJson<T>(ReadOnlySpan<byte> utf8)
    {
        return JsonSerializer.Deserialize<T>(utf8);
    }
}

public sealed class TcpTransportChannel : ITransportChannel
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public ConnectionMode Mode { get; }
    public bool IsConnected => _client.Connected;

    public TcpTransportChannel(TcpClient client, ConnectionMode mode)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        Mode = mode;
    }

    public async Task SendPacketAsync(PacketType type, ReadOnlyMemory<byte> payload, bool compress = false, CancellationToken ct = default)
    {
        byte flags = 0;
        byte[] data;
        if (compress && payload.Length > 256)
        {
            data = FrameCodec.Compress(payload.Span);
            flags |= FrameCodec.FlagCompressed;
        }
        else
        {
            data = payload.ToArray();
        }

        var raw = FrameCodec.EncodePlainPacket(type, data, flags);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(raw, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<SconPacket?> ReceivePacketAsync(CancellationToken ct = default)
    {
        var header = new byte[FrameCodec.WireHeaderBytes];
        if (!await ReadExactAsync(_stream, header, ct).ConfigureAwait(false))
            return null;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        if (magic != ProtocolConstants.Magic)
            throw new InvalidDataException($"Invalid SCON magic: 0x{magic:X8}");

        var type = (PacketType)header[5];
        byte flags = header[6];
        int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(7, 4));
        if (len < 0 || len > ProtocolConstants.MaxPacketPayloadBytes)
            throw new InvalidDataException($"Invalid packet length: {len}");

        var payload = new byte[len];
        if (len > 0 && !await ReadExactAsync(_stream, payload, ct).ConfigureAwait(false))
            return null;

        if ((flags & FrameCodec.FlagCompressed) != 0)
        {
            payload = FrameCodec.Decompress(payload);
            flags &= unchecked((byte)~FrameCodec.FlagCompressed);
        }

        return new SconPacket(type, flags, payload);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    public ValueTask DisposeAsync()
    {
        _sendLock.Dispose();
        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class WebSocketTransportChannel : ITransportChannel
{
    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public ConnectionMode Mode { get; }
    public bool IsConnected => _ws.State == WebSocketState.Open;

    public WebSocketTransportChannel(WebSocket ws, ConnectionMode mode = ConnectionMode.WssRelay)
    {
        _ws = ws;
        Mode = mode;
    }

    public async Task SendPacketAsync(PacketType type, ReadOnlyMemory<byte> payload, bool compress = false, CancellationToken ct = default)
    {
        byte flags = 0;
        byte[] data;
        if (compress && payload.Length > 256)
        {
            data = FrameCodec.Compress(payload.Span);
            flags |= FrameCodec.FlagCompressed;
        }
        else
        {
            data = payload.ToArray();
        }

        var raw = FrameCodec.EncodePlainPacket(type, data, flags);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(raw, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<SconPacket?> ReceivePacketAsync(CancellationToken ct = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var res = await _ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (res.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buffer, 0, res.Count);
                if (res.EndOfMessage) break;
                if (ms.Length > ProtocolConstants.MaxPacketPayloadBytes + FrameCodec.WireHeaderBytes)
                    throw new InvalidDataException("WebSocket message exceeded max packet limit.");
            }

            var raw = ms.ToArray();
            if (raw.Length < FrameCodec.WireHeaderBytes) return null;
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4));
            if (magic != ProtocolConstants.Magic)
                throw new InvalidDataException($"Invalid SCON magic on WebSocket: 0x{magic:X8}");

            var type = (PacketType)raw[5];
            byte flags = raw[6];
            int len = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(7, 4));
            if (len < 0 || FrameCodec.WireHeaderBytes + len > raw.Length)
                throw new InvalidDataException($"Invalid WS SCON frame length: {len}");

            var payload = raw.AsSpan(FrameCodec.WireHeaderBytes, len).ToArray();
            if ((flags & FrameCodec.FlagCompressed) != 0)
            {
                payload = FrameCodec.Decompress(payload);
                flags &= unchecked((byte)~FrameCodec.FlagCompressed);
            }

            return new SconPacket(type, flags, payload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        _sendLock.Dispose();
        _ws.Dispose();
    }
}

/// <summary>
/// Wraps any ITransportChannel with AES-256-GCM encryption once ECDH handshake completes.
/// </summary>
public sealed class EncryptedTransportChannel : ITransportChannel
{
    private readonly ITransportChannel _inner;
    private readonly CryptoSession _crypto;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public ConnectionMode Mode => _inner.Mode;
    public bool IsConnected => _inner.IsConnected;
    public string? InnerCloseReason => (_inner as MqttPipeTransportChannel)?.CloseReason;

    public EncryptedTransportChannel(ITransportChannel inner, CryptoSession crypto)
    {
        _inner = inner;
        _crypto = crypto;
    }

    public async Task SendPacketAsync(PacketType type, ReadOnlyMemory<byte> payload, bool compress = false, CancellationToken ct = default)
    {
        // Pack [1B InnerType][1B InnerFlags][Payload] inside encrypted envelope
        byte innerFlags = 0;
        byte[] body;
        if (compress && payload.Length > 256)
        {
            body = FrameCodec.Compress(payload.Span);
            innerFlags |= FrameCodec.FlagCompressed;
        }
        else
        {
            body = payload.ToArray();
        }

        var plainEnvelope = new byte[2 + body.Length];
        plainEnvelope[0] = (byte)type;
        plainEnvelope[1] = innerFlags;
        Buffer.BlockCopy(body, 0, plainEnvelope, 2, body.Length);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cipher = _crypto.Encrypt(plainEnvelope);
            await _inner.SendPacketAsync(type, cipher, compress: false, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<SconPacket?> ReceivePacketAsync(CancellationToken ct = default)
    {
        var raw = await _inner.ReceivePacketAsync(ct).ConfigureAwait(false);
        if (raw is null) return null;
        if (raw.Type == PacketType.Disconnect && raw.Payload.Length == 0)
            return raw;

        var plainEnvelope = _crypto.Decrypt(raw.Payload);
        if (plainEnvelope.Length < 2)
            throw new InvalidDataException("Encrypted envelope malformed.");

        var innerType = (PacketType)plainEnvelope[0];
        byte innerFlags = plainEnvelope[1];
        var payload = plainEnvelope.AsSpan(2).ToArray();
        if ((innerFlags & FrameCodec.FlagCompressed) != 0)
        {
            payload = FrameCodec.Decompress(payload);
            innerFlags &= unchecked((byte)~FrameCodec.FlagCompressed);
        }
        return new SconPacket(innerType, innerFlags, payload);
    }

    public async ValueTask DisposeAsync()
    {
        _sendLock.Dispose();
        _crypto.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
