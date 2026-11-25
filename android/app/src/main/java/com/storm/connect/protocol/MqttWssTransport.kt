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
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit

/**
 * RFC 3.1.1 MQTT-over-WebSocket client & chunked SCON pipe matching StormConnect.Protocol.MqttWssClient.
 * Allows Android <-> Windows connections by 9-digit STORM ID with zero private server.
 */
class MqttWssClient {
    private val rxMessages = Channel<Pair<String, ByteArray>>(Channel.UNLIMITED)
    private val connAck = Channel<Boolean>(1)
    private val subAcks = ConcurrentHashMap<Int, kotlinx.coroutines.CompletableDeferred<Boolean>>()
    private val streamBuf = ByteArrayOutputStream()
    private val client = OkHttpClient.Builder()
        .pingInterval(15, TimeUnit.SECONDS)
        .connectTimeout(8, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .build()

    @Volatile
    var isConnected = false
        private set

    private lateinit var ws: WebSocket
    private var nextPacketId = 1

    suspend fun connect(brokerUrl: String, clientId: String): Boolean {
        val openChan = Channel<Boolean>(1)
        val req = Request.Builder()
            .url(brokerUrl)
            .header("Sec-WebSocket-Protocol", "mqtt")
            .build()

        ws = client.newWebSocket(req, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                isConnected = true
                openChan.trySend(true)
            }

            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                parseIncomingBytes(bytes.toByteArray())
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                isConnected = false
                rxMessages.close()
                openChan.trySend(false)
                connAck.trySend(false)
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                isConnected = false
                rxMessages.close(t)
                openChan.trySend(false)
                connAck.trySend(false)
            }
        })

        if (!openChan.receive()) return false
        sendConnect(clientId)
        return connAck.receive()
    }

    private fun parseIncomingBytes(chunk: ByteArray) {
        synchronized(streamBuf) {
            streamBuf.write(chunk)
            val all = streamBuf.toByteArray()
            var pos = 0
            while (pos + 2 <= all.size) {
                val hdr = all[pos].toInt() and 0xFF
                var idx = pos + 1
                var mult = 1
                var remLen = 0
                var b: Int
                var ok = true
                do {
                    if (idx >= all.size) { ok = false; break }
                    b = all[idx++].toInt() and 0xFF
                    remLen += (b and 127) * mult
                    mult *= 128
                } while ((b and 128) != 0)
                if (!ok || idx + remLen > all.size) break

                val body = all.copyOfRange(idx, idx + remLen)
                pos = idx + remLen

                when (hdr ushr 4) {
                    2 -> connAck.trySend(body.size >= 2 && body[1].toInt() == 0)
                    9 -> if (body.size >= 2) {
                        val pid = ((body[0].toInt() and 0xFF) shl 8) or (body[1].toInt() and 0xFF)
                        subAcks.remove(pid)?.complete(true)
                    }
                    3 -> decodePublish(hdr, body)
                }
            }
            if (pos > 0) {
                val rem = all.size - pos
                streamBuf.reset()
                if (rem > 0) streamBuf.write(all, pos, rem)
            }
        }
    }

    private fun decodePublish(hdr: Int, body: ByteArray) {
        if (body.size < 2) return
        val tLen = ((body[0].toInt() and 0xFF) shl 8) or (body[1].toInt() and 0xFF)
        var off = 2 + tLen
        if (off > body.size) return
        val topic = String(body, 2, tLen, Charsets.UTF_8)
        val qos = (hdr ushr 1) and 0x03
        if (qos > 0) off += 2
        if (off > body.size) return
        rxMessages.trySend(topic to body.copyOfRange(off, body.size))
    }

    private fun sendConnect(clientId: String) {
        val cid = clientId.toByteArray(Charsets.UTF_8)
        val remLen = 10 + 2 + cid.size
        val out = ByteArrayOutputStream(remLen + 5)
        out.write(0x10)
        writeRemLen(out, remLen)
        out.write(byteArrayOf(0x00, 0x04, 'M'.code.toByte(), 'Q'.code.toByte(), 'T'.code.toByte(), 'T'.code.toByte(), 0x04, 0x02, 0x00, 0x1E))
        out.write((cid.size ushr 8) and 0xFF)
        out.write(cid.size and 0xFF)
        out.write(cid)
        val b = out.toByteArray()
        ws.send(b.toByteString(0, b.size))
    }

    suspend fun subscribe(topic: String) {
        val pid = (nextPacketId++ % 65000) + 1
        val def = kotlinx.coroutines.CompletableDeferred<Boolean>()
        subAcks[pid] = def
        val t = topic.toByteArray(Charsets.UTF_8)
        val remLen = 2 + 2 + t.size + 1
        val out = ByteArrayOutputStream(remLen + 5)
        out.write(0x82)
        writeRemLen(out, remLen)
        out.write((pid ushr 8) and 0xFF)
        out.write(pid and 0xFF)
        out.write((t.size ushr 8) and 0xFF)
        out.write(t.size and 0xFF)
        out.write(t)
        out.write(0x00)
        val b = out.toByteArray()
        ws.send(b.toByteString(0, b.size))
        kotlinx.coroutines.withTimeoutOrNull(4500) { def.await() }
        subAcks.remove(pid)
    }

    fun publish(topic: String, payload: ByteArray) {
        val t = topic.toByteArray(Charsets.UTF_8)
        val remLen = 2 + t.size + payload.size
        val out = ByteArrayOutputStream(remLen + 5)
        out.write(0x30)
        writeRemLen(out, remLen)
        out.write((t.size ushr 8) and 0xFF)
        out.write(t.size and 0xFF)
        out.write(t)
        out.write(payload)
        val b = out.toByteArray()
        ws.send(b.toByteString(0, b.size))
    }

    suspend fun readMessage(): Pair<String, ByteArray>? = try { rxMessages.receive() } catch (_: Exception) { null }

    fun close() {
        isConnected = false
        try { ws.close(1000, "bye") } catch (_: Exception) {}
        try { client.dispatcher.executorService.shutdown() } catch (_: Exception) {}
    }

    private fun writeRemLen(out: ByteArrayOutputStream, len0: Int) {
        var len = len0
        do {
            var d = len % 128
            len /= 128
            if (len > 0) d = d or 0x80
            out.write(d)
        } while (len > 0)
    }

    companion object {
        val PUBLIC_BROKERS = listOf(
            "wss://broker.emqx.io:8084/mqtt",
            "wss://broker.hivemq.com:8884/mqtt",
            "wss://test.mosquitto.org:8081/mqtt"
        )
    }
}

class MqttPipeTransportChannel(
    private val mqtt: MqttWssClient,
    sessionToken: String,
    isInitiator: Boolean
) : TransportChannel {
    private val txTopic = if (isInitiator) "sc/v1/p/$sessionToken/c2h" else "sc/v1/p/$sessionToken/h2c"
    private val rxTopic = if (isInitiator) "sc/v1/p/$sessionToken/h2c" else "sc/v1/p/$sessionToken/c2h"
    private var nextMsgId = 1
    private val assemblies = ConcurrentHashMap<Int, Pair<Int, Array<ByteArray?>>>()

    override val modeLabel: String = "WssMesh"
    override val isConnected: Boolean get() = mqtt.isConnected

    suspend fun subscribeRx() = mqtt.subscribe(rxTopic)

    override suspend fun sendPacket(type: PacketType, payload: ByteArray, compress: Boolean) {
        var flags: Byte = 0
        val data = if (compress && payload.size > 256) {
            flags = ProtocolConstants.FLAG_COMPRESSED
            FrameCodec.compress(payload)
        } else payload
        val rawFrame = FrameCodec.encodePlain(type, data, flags)
        val msgId = nextMsgId++
        val maxBody = 28 * 1024
        val partCount = maxOf(1, (rawFrame.size + maxBody - 1) / maxBody)

        for (p in 0 until partCount) {
            val off = p * maxBody
            val len = minOf(maxBody, rawFrame.size - off)
            val chunk = ByteArray(12 + len)
            val bb = ByteBuffer.wrap(chunk).order(ByteOrder.LITTLE_ENDIAN)
            bb.putInt(0, msgId)
            bb.putShort(4, p.toShort())
            bb.putShort(6, partCount.toShort())
            bb.putInt(8, rawFrame.size)
            System.arraycopy(rawFrame, off, chunk, 12, len)
            mqtt.publish(txTopic, chunk)
        }
    }

    override suspend fun receivePacket(): SconPacket? {
        while (mqtt.isConnected) {
            val (topic, buf) = mqtt.readMessage() ?: return null
            if (topic != rxTopic || buf.size < 12) continue

            val bb = ByteBuffer.wrap(buf).order(ByteOrder.LITTLE_ENDIAN)
            val msgId = bb.getInt(0)
            val partIdx = bb.getShort(4).toInt() and 0xFFFF
            val partCount = bb.getShort(6).toInt() and 0xFFFF
            val totalLen = bb.getInt(8)
            if (partCount == 0 || partIdx >= partCount || totalLen <= 0) continue

            val fullRaw: ByteArray? = if (partCount == 1) {
                buf.copyOfRange(12, buf.size)
            } else {
                val entry = assemblies.getOrPut(msgId) { totalLen to arrayOfNulls<ByteArray>(partCount) }
                entry.second[partIdx] = buf.copyOfRange(12, buf.size)
                if (entry.second.all { it != null }) {
                    assemblies.remove(msgId)
                    ByteArray(totalLen).also { dst ->
                        var pos = 0
                        for (slice in entry.second) {
                            val s = slice!!
                            val c = minOf(s.size, totalLen - pos)
                            System.arraycopy(s, 0, dst, pos, c)
                            pos += c
                        }
                    }
                } else null
            }

            if (fullRaw == null || fullRaw.size < ProtocolConstants.WIRE_HEADER_BYTES) continue
            val hdr = ByteBuffer.wrap(fullRaw).order(ByteOrder.LITTLE_ENDIAN)
            if (hdr.getInt(0) != ProtocolConstants.MAGIC) continue
            val type = PacketType.fromByte(fullRaw[5]) ?: continue
            var flags = fullRaw[6]
            val len = hdr.getInt(7)
            if (len < 0 || ProtocolConstants.WIRE_HEADER_BYTES + len > fullRaw.size) continue
            var payload = fullRaw.copyOfRange(ProtocolConstants.WIRE_HEADER_BYTES, ProtocolConstants.WIRE_HEADER_BYTES + len)
            if ((flags.toInt() and ProtocolConstants.FLAG_COMPRESSED.toInt()) != 0) {
                payload = FrameCodec.decompress(payload)
                flags = (flags.toInt() and ProtocolConstants.FLAG_COMPRESSED.toInt().inv()).toByte()
            }
            return SconPacket(type, flags, payload)
        }
        return null
    }

    override fun close() = mqtt.close()
}
