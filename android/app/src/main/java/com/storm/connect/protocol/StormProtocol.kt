package com.storm.connect.protocol

import kotlinx.coroutines.channels.Channel
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.TimeUnit
import java.util.zip.Deflater
import java.util.zip.Inflater

object ProtocolConstants {
    const val MAGIC = 0x4E4F4353 // "SCON"
    const val VERSION: Byte = 1
    const val WIRE_HEADER_BYTES = 11
    const val FLAG_COMPRESSED: Byte = 0x01
    const val DEFAULT_DIRECT_PORT = 7440
    const val DEFAULT_LAN_PORT = 7445
    const val DEFAULT_RELAY_URL = ""
    const val FILE_CHUNK_BYTES = 256 * 1024
}

enum class PacketType(val id: Byte) {
    Hello(1),
    KeyExchange(2),
    AuthRequest(3),
    AuthResponse(4),
    ConsentRequest(5),
    ConsentDecision(6),
    MonitorList(7),
    MonitorSelect(8),
    VideoFrameFull(9),
    VideoFrameTiles(10),
    CursorUpdate(11),
    InputMouse(12),
    InputKeyboard(13),
    ClipboardData(14),
    FileListReq(15),
    FileListResp(16),
    FileTransferInit(17),
    FileTransferChunk(18),
    FileTransferAck(19),
    FileOperationReq(20),
    FileOperationResp(21),
    AudioFrame(22),
    ChatMessage(23),
    Ping(24),
    Pong(25),
    SessionAction(26),
    RelayRegister(27),
    RelayRegistered(28),
    RelayConnect(29),
    RelayPaired(30),
    RelayError(31),
    UdpPunchEndpoint(32),
    Disconnect(33);

    companion object {
        fun fromByte(b: Byte): PacketType? = entries.firstOrNull { it.id == b }
    }
}

data class SconPacket(val type: PacketType, val flags: Byte, val payload: ByteArray)

interface TransportChannel {
    val modeLabel: String
    val isConnected: Boolean
    suspend fun sendPacket(type: PacketType, payload: ByteArray, compress: Boolean = false)
    suspend fun receivePacket(): SconPacket?
    fun close()
}

object FrameCodec {
    fun encodePlain(type: PacketType, payload: ByteArray, flags: Byte = 0): ByteArray {
        val out = ByteArray(ProtocolConstants.WIRE_HEADER_BYTES + payload.size)
        val bb = ByteBuffer.wrap(out).order(ByteOrder.LITTLE_ENDIAN)
        bb.putInt(ProtocolConstants.MAGIC)
        out[4] = ProtocolConstants.VERSION
        out[5] = type.id
        out[6] = flags
        bb.putInt(7, payload.size)
        System.arraycopy(payload, 0, out, ProtocolConstants.WIRE_HEADER_BYTES, payload.size)
        return out
    }

    fun compress(raw: ByteArray): ByteArray {
        val deflater = Deflater(Deflater.BEST_SPEED, true) // raw deflate matching .NET DeflateStream
        deflater.setInput(raw)
        deflater.finish()
        val out = ByteArrayOutputStream(raw.size)
        val buf = ByteArray(4096)
        while (!deflater.finished()) {
            val c = deflater.deflate(buf)
            out.write(buf, 0, c)
        }
        deflater.end()
        return out.toByteArray()
    }

    fun decompress(compressed: ByteArray): ByteArray {
        val inflater = Inflater(true) // raw deflate matching .NET DeflateStream
        inflater.setInput(compressed)
        val out = ByteArrayOutputStream(compressed.size * 2)
        val buf = ByteArray(8192)
        while (!inflater.finished()) {
            val c = inflater.inflate(buf)
            if (c == 0 && inflater.needsInput()) break
            out.write(buf, 0, c)
        }
        inflater.end()
        return out.toByteArray()
    }
}

class TcpTransportChannel(private val socket: Socket, override val modeLabel: String) : TransportChannel {
    private val input: DataInputStream = DataInputStream(socket.getInputStream())
    private val output: OutputStream = socket.getOutputStream()
    private val sendLock = Any()

    override val isConnected: Boolean
        get() = socket.isConnected && !socket.isClosed

    override suspend fun sendPacket(type: PacketType, payload: ByteArray, compress: Boolean) {
        var flags: Byte = 0
        val data = if (compress && payload.size > 256) {
            flags = ProtocolConstants.FLAG_COMPRESSED
            FrameCodec.compress(payload)
        } else payload
        val raw = FrameCodec.encodePlain(type, data, flags)
        synchronized(sendLock) {
            output.write(raw)
            output.flush()
        }
    }

    override suspend fun receivePacket(): SconPacket? {
        return try {
            val hdr = ByteArray(ProtocolConstants.WIRE_HEADER_BYTES)
            input.readFully(hdr)
            val bb = ByteBuffer.wrap(hdr).order(ByteOrder.LITTLE_ENDIAN)
            val magic = bb.getInt(0)
            if (magic != ProtocolConstants.MAGIC) return null
            val type = PacketType.fromByte(hdr[5]) ?: return null
            var flags = hdr[6]
            val len = bb.getInt(7)
            if (len < 0 || len > 32 * 1024 * 1024) return null
            var payload = ByteArray(len)
            if (len > 0) input.readFully(payload)
            if ((flags.toInt() and ProtocolConstants.FLAG_COMPRESSED.toInt()) != 0) {
                payload = FrameCodec.decompress(payload)
                flags = (flags.toInt() and ProtocolConstants.FLAG_COMPRESSED.toInt().inv()).toByte()
            }
            SconPacket(type, flags, payload)
        } catch (_: Exception) {
            null
        }
    }

    override fun close() {
        try { socket.close() } catch (_: Exception) {}
    }

    companion object {
        fun connect(host: String, port: Int, mode: String): TcpTransportChannel {
            val sock = Socket()
            sock.tcpNoDelay = true
            sock.connect(InetSocketAddress(host, port), 5000)
            return TcpTransportChannel(sock, mode)
        }
    }
}

class WsTransportChannel(url: String, override val modeLabel: String = "WssRelay") : TransportChannel {
    private val rxChannel = Channel<ByteArray>(Channel.UNLIMITED)
    private val openSignal = Channel<Boolean>(1)
    private val client = OkHttpClient.Builder()
        .pingInterval(15, TimeUnit.SECONDS)
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .build()

    @Volatile
    private var open = false
    private val ws: WebSocket

    init {
        val req = Request.Builder().url(url).build()
        ws = client.newWebSocket(req, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                open = true
                openSignal.trySend(true)
            }

            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                rxChannel.trySend(bytes.toByteArray())
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                open = false
                rxChannel.close()
                openSignal.trySend(false)
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                open = false
                rxChannel.close(t)
                openSignal.trySend(false)
            }
        })
    }

    suspend fun awaitOpen(): Boolean = openSignal.receive()

    override val isConnected: Boolean get() = open

    override suspend fun sendPacket(type: PacketType, payload: ByteArray, compress: Boolean) {
        var flags: Byte = 0
        val data = if (compress && payload.size > 256) {
            flags = ProtocolConstants.FLAG_COMPRESSED
            FrameCodec.compress(payload)
        } else payload
        val raw = FrameCodec.encodePlain(type, data, flags)
        ws.send(raw.toByteString(0, raw.size))
    }

    override suspend fun receivePacket(): SconPacket? {
        val raw = try { rxChannel.receive() } catch (_: Exception) { return null }
        if (raw.size < ProtocolConstants.WIRE_HEADER_BYTES) return null
        val bb = ByteBuffer.wrap(raw).order(ByteOrder.LITTLE_ENDIAN)
        if (bb.getInt(0) != ProtocolConstants.MAGIC) return null
        val type = PacketType.fromByte(raw[5]) ?: return null
        var flags = raw[6]
        val len = bb.getInt(7)
        if (len < 0 || ProtocolConstants.WIRE_HEADER_BYTES + len > raw.size) return null
        var payload = raw.copyOfRange(ProtocolConstants.WIRE_HEADER_BYTES, ProtocolConstants.WIRE_HEADER_BYTES + len)
        if ((flags.toInt() and ProtocolConstants.FLAG_COMPRESSED.toInt()) != 0) {
            payload = FrameCodec.decompress(payload)
            flags = (flags.toInt() and ProtocolConstants.FLAG_COMPRESSED.toInt().inv()).toByte()
        }
        return SconPacket(type, flags, payload)
    }

    override fun close() {
        open = false
        try { ws.close(1000, "bye") } catch (_: Exception) {}
        try { client.dispatcher.executorService.shutdown() } catch (_: Exception) {}
    }
}

class EncryptedTransportChannel(
    private val inner: TransportChannel,
    private val crypto: StormCrypto
) : TransportChannel {
    override val modeLabel: String get() = inner.modeLabel
    override val isConnected: Boolean get() = inner.isConnected

    override suspend fun sendPacket(type: PacketType, payload: ByteArray, compress: Boolean) {
        var innerFlags: Byte = 0
        val body = if (compress && payload.size > 256) {
            innerFlags = ProtocolConstants.FLAG_COMPRESSED
            FrameCodec.compress(payload)
        } else payload

        val plainEnv = ByteArray(2 + body.size)
        plainEnv[0] = type.id
        plainEnv[1] = innerFlags
        System.arraycopy(body, 0, plainEnv, 2, body.size)

        val cipher = crypto.encrypt(plainEnv)
        inner.sendPacket(type, cipher, compress = false)
    }

    override suspend fun receivePacket(): SconPacket? {
        val raw = inner.receivePacket() ?: return null
        if (raw.type == PacketType.Disconnect && raw.payload.isEmpty()) return raw

        val plainEnv = crypto.decrypt(raw.payload)
        if (plainEnv.size < 2) return null
        val innerType = PacketType.fromByte(plainEnv[0]) ?: return null
        var innerFlags = plainEnv[1]
        var payload = plainEnv.copyOfRange(2, plainEnv.size)
        if ((innerFlags.toInt() and ProtocolConstants.FLAG_COMPRESSED.toInt()) != 0) {
            payload = FrameCodec.decompress(payload)
            innerFlags = (innerFlags.toInt() and ProtocolConstants.FLAG_COMPRESSED.toInt().inv()).toByte()
        }
        return SconPacket(innerType, innerFlags, payload)
    }

    override fun close() = inner.close()
}

object WakeOnLan {
    fun sendMagicPacket(mac: String) {
        val hex = mac.filter { it.isDigit() || it.lowercaseChar() in 'a'..'f' }
        require(hex.length == 12) { "Invalid MAC" }
        val macBytes = ByteArray(6) { i -> hex.substring(i * 2, i * 2 + 2).toInt(16).toByte() }
        val pkt = ByteArray(6 + 16 * 6)
        for (i in 0 until 6) pkt[i] = 0xFF.toByte()
        for (i in 1..16) System.arraycopy(macBytes, 0, pkt, i * 6, 6)
        DatagramSocket().use { sock ->
            sock.broadcast = true
            val bcast = InetAddress.getByName("255.255.255.255")
            sock.send(DatagramPacket(pkt, pkt.size, bcast, 9))
            sock.send(DatagramPacket(pkt, pkt.size, bcast, 7))
        }
    }
}
