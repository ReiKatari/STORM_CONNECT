using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace StormConnect.Protocol;

/// <summary>
/// Minimal, zero-dependency RFC-compliant MQTT 3.1.1 client over WebSocket (Sec-WebSocket-Protocol: mqtt)
/// with strict per-PacketId SUBACK awaiting and 28 KB broker-safe chunking.
/// </summary>
public sealed class MqttWssClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<(string Topic, byte[] Payload)> _rxMessages = Channel.CreateUnbounded<(string, byte[])>();
    private readonly TaskCompletionSource _connAckTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource> _pendingSubAcks = new();
    private int _nextPacketId = 1;
    private Task? _rxTask;
    private Task? _pingTask;

    public bool IsConnected => _ws.State == WebSocketState.Open;
    public string BrokerUrl { get; private set; } = string.Empty;
    public string? CloseReason { get; private set; }

    public async Task ConnectAsync(string brokerUrl, string clientId, CancellationToken ct = default)
    {
        BrokerUrl = brokerUrl;
        _ws.Options.AddSubProtocol("mqtt");
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _ws.ConnectAsync(new Uri(brokerUrl), ct).ConfigureAwait(false);

        _rxTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        var connectPkt = BuildConnectPacket(clientId);
        await SendRawAsync(connectPkt, ct).ConfigureAwait(false);

        using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ackCts.CancelAfter(TimeSpan.FromSeconds(6));
        await _connAckTcs.Task.WaitAsync(ackCts.Token).ConfigureAwait(false);

        _pingTask = Task.Run(() => PingLoopAsync(_cts.Token));
    }

    public async Task SubscribeAsync(string topic, CancellationToken ct = default)
    {
        int raw = Interlocked.Increment(ref _nextPacketId);
        ushort pid = (ushort)((raw % 65000) + 1);
        var subTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSubAcks[pid] = subTcs;

        try
        {
            var pkt = BuildSubscribePacket(pid, topic);
            await SendRawAsync(pkt, ct).ConfigureAwait(false);

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(TimeSpan.FromSeconds(5));
            await subTcs.Task.WaitAsync(waitCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _pendingSubAcks.TryRemove(pid, out _);
        }
    }

    public Task PublishAsync(string topic, ReadOnlySpan<byte> payload, CancellationToken ct = default)
    {
        var pkt = BuildPublishPacket(topic, payload);
        return SendRawAsync(pkt, ct);
    }

    public async Task<(string Topic, byte[] Payload)?> ReadMessageAsync(CancellationToken ct = default)
    {
        try
        {
            return await _rxMessages.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    private async Task SendRawAsync(byte[] bytes, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(bytes, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        byte[] pingReq = [0xC0, 0x00];
        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(15000, ct).ConfigureAwait(false);
                await SendRawAsync(pingReq, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var streamBuf = new MemoryStream();
        long readPos = 0;
        var chunk = ArrayPool<byte>.Shared.Rent(65536);

        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var res = await _ws.ReceiveAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    CloseReason = $"WS Closed ({_ws.CloseStatus}: {_ws.CloseStatusDescription})";
                    break;
                }

                if (res.Count > 0)
                {
                    streamBuf.Position = streamBuf.Length;
                    streamBuf.Write(chunk, 0, res.Count);
                }

                while (TryParseNextMqttPacket(streamBuf, ref readPos, out byte hdr, out byte[] body))
                {
                    byte pktType = (byte)(hdr >> 4);
                    switch (pktType)
                    {
                        case 2: // CONNACK
                            if (body.Length >= 2 && body[1] == 0) _connAckTcs.TrySetResult();
                            else _connAckTcs.TrySetException(new InvalidOperationException($"MQTT CONNACK rejected ({(body.Length >= 2 ? body[1] : -1)})."));
                            break;
                        case 9: // SUBACK
                            if (body.Length >= 2)
                            {
                                ushort pid = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(0, 2));
                                if (_pendingSubAcks.TryRemove(pid, out var tcs))
                                    tcs.TrySetResult();
                            }
                            break;
                        case 3: // PUBLISH
                            if (TryDecodePublish(hdr, body, out var topic, out var msgPayload))
                                _rxMessages.Writer.TryWrite((topic, msgPayload));
                            break;
                    }
                }

                if (readPos > 0)
                {
                    int rem = (int)(streamBuf.Length - readPos);
                    if (rem == 0)
                    {
                        streamBuf.SetLength(0);
                    }
                    else
                    {
                        var arr = streamBuf.GetBuffer();
                        Buffer.BlockCopy(arr, (int)readPos, arr, 0, rem);
                        streamBuf.SetLength(rem);
                    }
                    readPos = 0;
                }
            }
        }
        catch (Exception ex)
        {
            CloseReason ??= ex.Message;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            _connAckTcs.TrySetCanceled();
            foreach (var kv in _pendingSubAcks) kv.Value.TrySetCanceled();
            _pendingSubAcks.Clear();
            _rxMessages.Writer.TryComplete();
        }
    }

    private static bool TryParseNextMqttPacket(MemoryStream ms, ref long pos, out byte header, out byte[] body)
    {
        header = 0;
        body = Array.Empty<byte>();
        long available = ms.Length - pos;
        if (available < 2) return false;

        var buf = ms.GetBuffer();
        int idx = (int)pos;
        header = buf[idx++];

        int multiplier = 1;
        int remLen = 0;
        byte encodedByte;
        int lenBytes = 0;
        do
        {
            if (idx >= ms.Length) return false;
            encodedByte = buf[idx++];
            remLen += (encodedByte & 127) * multiplier;
            multiplier *= 128;
            lenBytes++;
            if (lenBytes > 4) throw new InvalidDataException("Malformed MQTT remaining length.");
        } while ((encodedByte & 128) != 0);

        if (ms.Length - idx < remLen) return false;
        body = new byte[remLen];
        Buffer.BlockCopy(buf, idx, body, 0, remLen);
        pos = idx + remLen;
        return true;
    }

    private static bool TryDecodePublish(byte header, byte[] body, out string topic, out byte[] payload)
    {
        topic = string.Empty;
        payload = Array.Empty<byte>();
        if (body.Length < 2) return false;
        int topicLen = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(0, 2));
        int off = 2 + topicLen;
        if (off > body.Length) return false;
        topic = Encoding.UTF8.GetString(body, 2, topicLen);
        int qos = (header >> 1) & 0x03;
        if (qos > 0) off += 2;
        if (off > body.Length) return false;
        payload = body.AsSpan(off).ToArray();
        return true;
    }

    private static byte[] BuildConnectPacket(string clientId)
    {
        var cidBytes = Encoding.UTF8.GetBytes(clientId);
        int remLen = 10 + 2 + cidBytes.Length;
        using var ms = new MemoryStream(remLen + 5);
        ms.WriteByte(0x10);
        WriteRemainingLength(ms, remLen);
        ms.Write(new byte[] { 0x00, 0x04, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 0x04, 0x02, 0x00, 0x1E });
        WriteUtf8String(ms, cidBytes);
        return ms.ToArray();
    }

    private static byte[] BuildSubscribePacket(ushort packetId, string topic)
    {
        var tBytes = Encoding.UTF8.GetBytes(topic);
        int remLen = 2 + 2 + tBytes.Length + 1;
        using var ms = new MemoryStream(remLen + 5);
        ms.WriteByte(0x82);
        WriteRemainingLength(ms, remLen);
        ms.WriteByte((byte)(packetId >> 8));
        ms.WriteByte((byte)(packetId & 0xFF));
        WriteUtf8String(ms, tBytes);
        ms.WriteByte(0x00);
        return ms.ToArray();
    }

    private static byte[] BuildPublishPacket(string topic, ReadOnlySpan<byte> payload)
    {
        var tBytes = Encoding.UTF8.GetBytes(topic);
        int remLen = 2 + tBytes.Length + payload.Length;
        using var ms = new MemoryStream(remLen + 5);
        ms.WriteByte(0x30);
        WriteRemainingLength(ms, remLen);
        WriteUtf8String(ms, tBytes);
        ms.Write(payload);
        return ms.ToArray();
    }

    private static void WriteUtf8String(Stream s, byte[] utf8)
    {
        s.WriteByte((byte)(utf8.Length >> 8));
        s.WriteByte((byte)(utf8.Length & 0xFF));
        s.Write(utf8);
    }

    private static void WriteRemainingLength(Stream s, int len)
    {
        do
        {
            byte digit = (byte)(len % 128);
            len /= 128;
            if (len > 0) digit |= 0x80;
            s.WriteByte(digit);
        } while (len > 0);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        _sendLock.Dispose();
        _ws.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// Full ITransportChannel over MQTT-over-WSS with automatic 28 KB packet chunking & reassembly.
/// </summary>
public sealed class MqttPipeTransportChannel : ITransportChannel
{
    private const int MaxChunkBody = 28 * 1024;
    private readonly MqttWssClient _mqtt;
    private readonly string _txTopic;
    private readonly string _rxTopic;
    private int _nextMsgId;
    private readonly ConcurrentDictionary<int, (int TotalLen, int ReceivedParts, byte[][] Parts)> _assemblies = new();

    public ConnectionMode Mode => ConnectionMode.WssRelay;
    public bool IsConnected => _mqtt.IsConnected;

    public MqttPipeTransportChannel(MqttWssClient mqtt, string sessionToken, bool isInitiator)
    {
        _mqtt = mqtt;
        _txTopic = isInitiator ? $"sc/v1/p/{sessionToken}/c2h" : $"sc/v1/p/{sessionToken}/h2c";
        _rxTopic = isInitiator ? $"sc/v1/p/{sessionToken}/h2c" : $"sc/v1/p/{sessionToken}/c2h";
    }

    public Task SubscribeRxAsync(CancellationToken ct = default) => _mqtt.SubscribeAsync(_rxTopic, ct);

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

        var rawFrame = FrameCodec.EncodePlainPacket(type, data, flags);
        int msgId = Interlocked.Increment(ref _nextMsgId);
        int partCount = Math.Max(1, (rawFrame.Length + MaxChunkBody - 1) / MaxChunkBody);

        for (int p = 0; p < partCount; p++)
        {
            int off = p * MaxChunkBody;
            int len = Math.Min(MaxChunkBody, rawFrame.Length - off);
            var chunk = new byte[12 + len];
            BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(0, 4), msgId);
            BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(4, 2), (ushort)p);
            BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(6, 2), (ushort)partCount);
            BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(8, 4), rawFrame.Length);
            Buffer.BlockCopy(rawFrame, off, chunk, 12, len);

            await _mqtt.PublishAsync(_txTopic, chunk, ct).ConfigureAwait(false);
        }
    }

    public async Task<SconPacket?> ReceivePacketAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested && _mqtt.IsConnected)
        {
            var msg = await _mqtt.ReadMessageAsync(ct).ConfigureAwait(false);
            if (msg is null) return null;
            if (!string.Equals(msg.Value.Topic, _rxTopic, StringComparison.Ordinal)) continue;

            var buf = msg.Value.Payload;
            if (buf.Length < 12) continue;

            int msgId = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
            ushort partIdx = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(4, 2));
            ushort partCount = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(6, 2));
            int totalLen = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8, 4));

            if (partCount == 0 || partIdx >= partCount || totalLen <= 0 || totalLen > ProtocolConstants.MaxPacketPayloadBytes + FrameCodec.WireHeaderBytes)
                continue;

            byte[]? fullRaw = null;
            if (partCount == 1)
            {
                fullRaw = buf.AsSpan(12).ToArray();
            }
            else
            {
                var entry = _assemblies.GetOrAdd(msgId, _ => (totalLen, 0, new byte[partCount][]));
                if (entry.Parts[partIdx] is null)
                {
                    entry.Parts[partIdx] = buf.AsSpan(12).ToArray();
                    int done = entry.ReceivedParts + 1;
                    entry = (entry.TotalLen, done, entry.Parts);
                    _assemblies[msgId] = entry;
                    if (done >= partCount)
                    {
                        _assemblies.TryRemove(msgId, out _);
                        fullRaw = new byte[totalLen];
                        int pos = 0;
                        for (int i = 0; i < partCount; i++)
                        {
                            var slice = entry.Parts[i];
                            if (slice is null) { fullRaw = null; break; }
                            int copy = Math.Min(slice.Length, totalLen - pos);
                            Buffer.BlockCopy(slice, 0, fullRaw, pos, copy);
                            pos += copy;
                        }
                    }
                }
            }

            if (fullRaw is null || fullRaw.Length < FrameCodec.WireHeaderBytes) continue;
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(fullRaw.AsSpan(0, 4));
            if (magic != ProtocolConstants.Magic) continue;

            var type = (PacketType)fullRaw[5];
            byte flags = fullRaw[6];
            int len = BinaryPrimitives.ReadInt32LittleEndian(fullRaw.AsSpan(7, 4));
            if (len < 0 || FrameCodec.WireHeaderBytes + len > fullRaw.Length) continue;

            var payload = fullRaw.AsSpan(FrameCodec.WireHeaderBytes, len).ToArray();
            if ((flags & FrameCodec.FlagCompressed) != 0)
            {
                payload = FrameCodec.Decompress(payload);
                flags &= unchecked((byte)~FrameCodec.FlagCompressed);
            }
            return new SconPacket(type, flags, payload);
        }
        return null;
    }

    public ValueTask DisposeAsync() => _mqtt.DisposeAsync();
}
