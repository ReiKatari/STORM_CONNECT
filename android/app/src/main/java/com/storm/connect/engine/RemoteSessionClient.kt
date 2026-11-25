package com.storm.connect.engine

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Paint
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import com.storm.connect.protocol.EncryptedTransportChannel
import com.storm.connect.protocol.MqttPipeTransportChannel
import com.storm.connect.protocol.MqttWssClient
import com.storm.connect.protocol.PacketType
import com.storm.connect.protocol.ProtocolConstants
import com.storm.connect.protocol.StormCrypto
import com.storm.connect.protocol.TcpTransportChannel
import com.storm.connect.protocol.TransportChannel
import com.storm.connect.protocol.WsTransportChannel
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.URI
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.Base64
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

data class DiscoveredLanDevice(
    val stormId: String,
    val hostName: String,
    val osName: String,
    val monitorCount: Int,
    val directPort: Int,
    val localIp: String,
    val lastSeenMs: Long
)

data class RemoteMonitor(
    val index: Int,
    val name: String,
    val width: Int,
    val height: Int,
    val isPrimary: Boolean
)

data class RemoteFileEntry(
    val name: String,
    val fullPath: String,
    val isDirectory: Boolean,
    val sizeBytes: Long
)

data class RemoteTelemetry(
    val mode: String = "Idle",
    val fps: Double = 0.0,
    val rttMs: Int = 0,
    val mbps: Double = 0.0,
    val width: Int = 0,
    val height: Int = 0
)

class LanDiscoveryManager(private val selfStormId: String) {
    private val devices = ConcurrentHashMap<String, DiscoveredLanDevice>()
    private var job: Job? = null
    private var scope: CoroutineScope? = null

    fun start(onUpdated: (List<DiscoveredLanDevice>) -> Unit) {
        if (job != null) return
        val sc = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        scope = sc
        job = sc.launch {
            launch {
                var sock: DatagramSocket? = null
                try {
                    sock = DatagramSocket(ProtocolConstants.DEFAULT_LAN_PORT).apply {
                        reuseAddress = true
                        broadcast = true
                    }
                    val buf = ByteArray(4096)
                    while (isActive) {
                        val pkt = DatagramPacket(buf, buf.size)
                        sock.receive(pkt)
                        val json = JSONObject(String(pkt.data, 0, pkt.length, Charsets.UTF_8))
                        val id = json.optString("StormId").filter { it.isDigit() }
                        if (id.length == 9 && id != selfStormId) {
                            val ip = json.optString("LocalIp").ifBlank { pkt.address.hostAddress ?: "" }
                            devices[id] = DiscoveredLanDevice(
                                id,
                                json.optString("HostName", "PC"),
                                json.optString("OsName", "Windows"),
                                json.optInt("MonitorCount", 1),
                                json.optInt("DirectPort", ProtocolConstants.DEFAULT_DIRECT_PORT),
                                ip,
                                System.currentTimeMillis()
                            )
                            onUpdated(currentDevices())
                        }
                    }
                } catch (_: Exception) {
                } finally {
                    try { sock?.close() } catch (_: Exception) {}
                }
            }

            while (isActive) {
                try {
                    val now = System.currentTimeMillis()
                    var pruned = false
                    devices.entries.removeIf { (now - it.value.lastSeenMs > 15000).also { r -> if (r) pruned = true } }
                    if (pruned) onUpdated(currentDevices())

                    val beacon = JSONObject().apply {
                        put("StormId", selfStormId)
                        put("HostName", android.os.Build.MODEL)
                        put("OsName", "Android")
                        put("MonitorCount", 1)
                        put("DirectPort", ProtocolConstants.DEFAULT_DIRECT_PORT)
                        put("LocalIp", "")
                        put("TimestampUnixMs", now)
                    }.toString().toByteArray(Charsets.UTF_8)

                    DatagramSocket().use { bcast ->
                        bcast.broadcast = true
                        bcast.send(DatagramPacket(beacon, beacon.size, InetAddress.getByName("255.255.255.255"), ProtocolConstants.DEFAULT_LAN_PORT))
                    }
                } catch (_: Exception) {}
                delay(2800)
            }
        }
    }

    fun currentDevices(): List<DiscoveredLanDevice> {
        val now = System.currentTimeMillis()
        return devices.values.filter { now - it.lastSeenMs < 15000 }.sortedBy { it.hostName }
    }

    fun stop() {
        job?.cancel()
        scope?.cancel()
        job = null
        scope = null
    }
}

class RemoteSessionClient(
    private val localStormId: String,
    private val downloadsDir: File
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var channel: TransportChannel? = null
    private var compositeBitmap: Bitmap? = null
    private val bmpLock = Any()
    private val paint = Paint()

    private var framesCount = 0
    private var bytesCount = 0L
    private var lastWindowMs = System.currentTimeMillis()
    private var lastRttMs = 0
    private var audioTrack: AudioTrack? = null
    private var audioEnabled = true

    private val activeDownloads = ConcurrentHashMap<String, Pair<FileOutputStream, File>>()

    var onStatus: ((String) -> Unit)? = null
    var onFrame: ((Bitmap) -> Unit)? = null
    var onMonitors: ((List<RemoteMonitor>) -> Unit)? = null
    var onTelemetry: ((RemoteTelemetry) -> Unit)? = null
    var onFileList: ((String, List<RemoteFileEntry>) -> Unit)? = null
    var onTransferStatus: ((String) -> Unit)? = null
    var onChat: ((String, String) -> Unit)? = null
    var onDisconnected: ((String) -> Unit)? = null

    val isConnected: Boolean get() = channel?.isConnected == true

    fun connect(
        targetInput: String,
        relayUrl: String,
        lanDevices: List<DiscoveredLanDevice>,
        promptSecret: suspend (String) -> String?
    ) {
        scope.launch {
            try {
                val trimmed = targetInput.trim()
                val digits = trimmed.filter { it.isDigit() }
                val rawChannel: TransportChannel = when {
                    trimmed.startsWith("SC1-", ignoreCase = true) -> {
                        onStatus?.invoke("Connecting via Direct Invite Code...")
                        connectViaInviteCode(trimmed, relayUrl, lanDevices)
                    }
                    digits.length != 9 || trimmed.contains('.') || trimmed.contains(':') -> {
                        onStatus?.invoke("Connecting Direct IP...")
                        val (host, port) = parseHostPort(trimmed, ProtocolConstants.DEFAULT_DIRECT_PORT)
                        TcpTransportChannel.connect(host, port, "DirectIp")
                    }
                    else -> {
                        val lan = lanDevices.firstOrNull { it.stormId == digits }
                        var tcp: TransportChannel? = null
                        if (lan != null) {
                            try {
                                onStatus?.invoke("Connecting LAN (${lan.localIp})...")
                                tcp = TcpTransportChannel.connect(lan.localIp, lan.directPort, "LanDirect")
                            } catch (_: Exception) {
                                tcp = null
                            }
                        }
                        tcp ?: connectViaRelay(digits, relayUrl)
                    }
                }

                // ECDH P-256 + AES-256-GCM Handshake (Client = Initiator)
                val crypto = StormCrypto()
                val hostKeyPkt = rawChannel.receivePacket() ?: error("Host disconnected at KeyExchange")
                require(hostKeyPkt.type == PacketType.KeyExchange)
                rawChannel.sendPacket(PacketType.KeyExchange, crypto.localPublicKeyUncompressed)
                crypto.deriveKeys(hostKeyPkt.payload, isInitiator = true)

                val enc = EncryptedTransportChannel(rawChannel, crypto)
                channel = enc

                val helloPkt = enc.receivePacket()
                val remoteName = if (helloPkt?.type == PacketType.Hello) {
                    JSONObject(String(helloPkt.payload, Charsets.UTF_8)).optString("HostName", trimmed)
                } else trimmed

                val chalPkt = enc.receivePacket() ?: error("Missing AuthChallenge")
                val chal = JSONObject(String(chalPkt.payload, Charsets.UTF_8))
                val salt = Base64.getDecoder().decode(chal.getString("Salt"))
                val nonce = Base64.getDecoder().decode(chal.getString("NonceChallenge"))
                val allowUnattended = chal.optBoolean("AllowUnattended", false)

                val secret = promptSecret(remoteName)
                val mode = if (!secret.isNullOrBlank() && allowUnattended && secret.length > 6) 1 else 0
                val proof = if (!secret.isNullOrBlank()) StormCrypto.computeChallengeProof(secret, salt, nonce) else null

                val authReq = JSONObject().apply {
                    put("ClientStormId", localStormId)
                    put("ClientName", android.os.Build.MODEL)
                    put("Mode", mode)
                    if (proof != null) put("PinOrPasswordProof", proof)
                }.toString().toByteArray(Charsets.UTF_8)
                enc.sendPacket(PacketType.AuthRequest, authReq)

                while (true) {
                    val respPkt = enc.receivePacket() ?: error("Closed during auth")
                    if (respPkt.type != PacketType.AuthResponse) continue
                    val resp = JSONObject(String(respPkt.payload, Charsets.UTF_8))
                    if (resp.optBoolean("PendingConsent", false)) {
                        onStatus?.invoke("Waiting for host approval...")
                        continue
                    }
                    if (!resp.optBoolean("Accepted", false)) {
                        error(resp.optString("Reason", "Access denied"))
                    }
                    break
                }

                audioEnabled = enc.modeLabel != "WssMesh"
                if (audioEnabled) initAudioTrack()
                onStatus?.invoke("Connected: $remoteName")
                launch { pingLoop() }
                receiveLoop(enc)
            } catch (e: Exception) {
                onDisconnected?.invoke(e.message ?: "Connection failed")
                disconnect()
            }
        }
    }

    private fun parseHostPort(spec: String, defaultPort: Int): Pair<String, Int> {
        val s = spec.trim()
        if (s.startsWith("[")) {
            val end = s.indexOf(']')
            if (end > 0) {
                val host = s.substring(1, end)
                val port = if (end + 2 <= s.length && s[end + 1] == ':') s.substring(end + 2).toIntOrNull() ?: defaultPort else defaultPort
                return host to port
            }
        }
        val first = s.indexOf(':')
        val last = s.lastIndexOf(':')
        if (first == last && last > 0) {
            val p = s.substring(last + 1).toIntOrNull()
            if (p != null) return s.substring(0, last) to p
        }
        return s to defaultPort
    }

    private suspend fun connectViaInviteCode(
        code: String,
        relayUrl: String,
        lanDevices: List<DiscoveredLanDevice>
    ): TransportChannel {
        val b64 = code.substring(4).replace('-', '+').replace('_', '/')
        val padded = when (b64.length % 4) {
            2 -> "$b64=="
            3 -> "$b64="
            else -> b64
        }
        val json = JSONObject(String(Base64.getDecoder().decode(padded), Charsets.UTF_8))
        val stormId = json.optString("StormId", "").filter { it.isDigit() }
        val directPort = json.optInt("DirectPort", ProtocolConstants.DEFAULT_DIRECT_PORT)

        val candidates = mutableListOf<Triple<String, Int, String>>()
        lanDevices.firstOrNull { it.stormId == stormId }?.let {
            candidates.add(Triple(it.localIp, it.directPort, "LanDirect"))
        }
        json.optString("LanIp").takeIf { it.isNotBlank() }?.let { candidates.add(Triple(it, directPort, "LanDirect")) }
        json.optString("VpnIp").takeIf { it.isNotBlank() }?.let { candidates.add(Triple(it, directPort, "DirectIp")) }
        json.optString("GlobalIpv6").takeIf { it.isNotBlank() }?.let { candidates.add(Triple(it, directPort, "DirectIp")) }
        val pub4 = json.optString("PublicIpv4")
        val mappedPort = json.optInt("MappedTcpPort", 0)
        if (pub4.isNotBlank() && mappedPort > 0) {
            candidates.add(Triple(pub4, mappedPort, "DirectIp"))
        }

        for ((host, port, mode) in candidates.distinctBy { "${it.first}:${it.second}" }) {
            try {
                return TcpTransportChannel.connect(host, port, mode)
            } catch (_: Exception) {}
        }
        return connectViaRelay(stormId, relayUrl)
    }

    private suspend fun connectViaRelay(targetId: String, relayUrl: String): TransportChannel {
        if (relayUrl.isNotBlank()) {
            try {
                onStatus?.invoke("Connecting via STORM CONNECT Relay...")
                val ctrl = WsTransportChannel(relayUrl, "WssRelay")
                if (ctrl.awaitOpen()) {
                    val reg = JSONObject().apply {
                        put("StormId", localStormId)
                        put("HostName", android.os.Build.MODEL)
                        put("OsName", "Android")
                        put("MonitorCount", 1)
                        put("DirectPort", ProtocolConstants.DEFAULT_DIRECT_PORT)
                    }.toString().toByteArray(Charsets.UTF_8)
                    ctrl.sendPacket(PacketType.RelayRegister, reg)

                    val conn = JSONObject().apply {
                        put("FromStormId", localStormId)
                        put("TargetStormId", targetId)
                    }.toString().toByteArray(Charsets.UTF_8)
                    ctrl.sendPacket(PacketType.RelayConnect, conn)

                    var token: String? = null
                    while (true) {
                        val pkt = ctrl.receivePacket() ?: break
                        if (pkt.type == PacketType.RelayError) break
                        if (pkt.type == PacketType.RelayPaired) {
                            token = JSONObject(String(pkt.payload, Charsets.UTF_8)).getString("SessionToken")
                            break
                        }
                    }
                    ctrl.close()
                    if (token != null) {
                        val uri = URI(relayUrl)
                        val pipeUrl = "${uri.scheme}://${uri.authority}/session/$token?role=initiator"
                        val pipe = WsTransportChannel(pipeUrl, "WssRelay")
                        if (pipe.awaitOpen()) return pipe
                    }
                }
            } catch (_: Exception) {}
        }

        // Built-in Zero-Server Public MQTT-WSS Mesh fallback
        onStatus?.invoke("Connecting via Serverless WSS Mesh...")
        for (brokerUrl in MqttWssClient.PUBLIC_BROKERS) {
            val mqtt = MqttWssClient()
            try {
                val cid = "sc_ac_${localStormId}_${UUID.randomUUID().toString().take(6)}"
                if (!mqtt.connect(brokerUrl, cid)) {
                    mqtt.close()
                    continue
                }
                val token = UUID.randomUUID().toString().replace("-", "")
                val pipe = MqttPipeTransportChannel(mqtt, token, isInitiator = true)
                pipe.subscribeRx()
                val invite = JSONObject().apply {
                    put("Token", token)
                    put("FromStormId", localStormId)
                }.toString().toByteArray(Charsets.UTF_8)
                mqtt.publish("sc/v1/s/$targetId", invite)
                return pipe
            } catch (_: Exception) {
                mqtt.close()
            }
        }
        error("Unable to reach any relay or mesh broker")
    }

    private suspend fun receiveLoop(enc: TransportChannel) {
        while (enc.isConnected) {
            val pkt = enc.receivePacket() ?: break
            bytesCount += pkt.payload.size
            when (pkt.type) {
                PacketType.MonitorList -> {
                    val arr = JSONArray(String(pkt.payload, Charsets.UTF_8))
                    val list = (0 until arr.length()).map { i ->
                        val o = arr.getJSONObject(i)
                        RemoteMonitor(o.getInt("Index"), o.getString("Name"), o.getInt("Width"), o.getInt("Height"), o.getBoolean("IsPrimary"))
                    }
                    onMonitors?.invoke(list)
                }

                PacketType.VideoFrameFull, PacketType.VideoFrameTiles -> {
                    decodeAndCompositeFrame(pkt.payload)
                    framesCount++
                }

                PacketType.AudioFrame -> {
                    if (audioEnabled) {
                        audioTrack?.write(pkt.payload, 0, pkt.payload.size)
                    }
                }

                PacketType.FileListResp -> {
                    val obj = JSONObject(String(pkt.payload, Charsets.UTF_8))
                    val cur = obj.optString("CurrentPath", "")
                    val arr = obj.optJSONArray("Entries") ?: JSONArray()
                    val list = (0 until arr.length()).map { i ->
                        val e = arr.getJSONObject(i)
                        RemoteFileEntry(e.getString("Name"), e.getString("FullPath"), e.getBoolean("IsDirectory"), e.optLong("SizeBytes", 0))
                    }
                    onFileList?.invoke(cur, list)
                }

                PacketType.FileTransferInit -> {
                    val obj = JSONObject(String(pkt.payload, Charsets.UTF_8))
                    if (!obj.optBoolean("IsUploadToRemote", true)) {
                        val tid = obj.getString("TransferId")
                        val fname = obj.getString("FileName")
                        downloadsDir.mkdirs()
                        val dst = File(downloadsDir, fname)
                        activeDownloads[tid] = Pair(FileOutputStream(dst), dst)
                        onTransferStatus?.invoke("Downloading $fname...")
                    }
                }

                PacketType.FileTransferChunk -> {
                    if (pkt.payload.size >= 36) {
                        val tid = String(pkt.payload, 0, 32, Charsets.US_ASCII).trim()
                        val dlen = ByteBuffer.wrap(pkt.payload, 32, 4).order(ByteOrder.LITTLE_ENDIAN).int
                        activeDownloads[tid]?.let { (fos, dst) ->
                            fos.write(pkt.payload, 36, dlen)
                            fos.flush()
                            onTransferStatus?.invoke("Saved: ${dst.absolutePath}")
                        }
                    }
                }

                PacketType.FileTransferAck -> {
                    val obj = JSONObject(String(pkt.payload, Charsets.UTF_8))
                    onTransferStatus?.invoke(obj.optString("Message", "Transfer finished"))
                }

                PacketType.ChatMessage -> {
                    val obj = JSONObject(String(pkt.payload, Charsets.UTF_8))
                    onChat?.invoke(obj.optString("SenderName", "Peer"), obj.optString("Text", ""))
                }

                PacketType.Pong -> {
                    if (pkt.payload.size >= 8) {
                        val sentMs = ByteBuffer.wrap(pkt.payload).order(ByteOrder.LITTLE_ENDIAN).long
                        lastRttMs = maxOf(1, (System.currentTimeMillis() - sentMs).toInt())
                    }
                }

                PacketType.Disconnect -> break
                else -> {}
            }
        }
        onDisconnected?.invoke("Session closed")
    }

    private fun decodeAndCompositeFrame(data: ByteArray) {
        if (data.size < 10) return
        val bb = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN)
        val frameW = bb.getInt(0)
        val frameH = bb.getInt(4)
        val count = bb.getShort(8).toInt() and 0xFFFF

        synchronized(bmpLock) {
            if (compositeBitmap == null || compositeBitmap!!.width != frameW || compositeBitmap!!.height != frameH) {
                compositeBitmap?.recycle()
                compositeBitmap = Bitmap.createBitmap(frameW, frameH, Bitmap.Config.ARGB_8888)
            }
            val canvas = Canvas(compositeBitmap!!)
            var off = 10
            for (i in 0 until count) {
                val x = bb.getInt(off)
                val y = bb.getInt(off + 4)
                val jlen = bb.getInt(off + 16)
                val tileBmp = BitmapFactory.decodeByteArray(data, off + 20, jlen)
                if (tileBmp != null) {
                    canvas.drawBitmap(tileBmp, x.toFloat(), y.toFloat(), paint)
                    tileBmp.recycle()
                }
                off += 20 + jlen
            }
            onFrame?.invoke(compositeBitmap!!)
        }
    }

    private suspend fun pingLoop() {
        while (isConnected) {
            try {
                val buf = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(System.currentTimeMillis()).array()
                channel?.sendPacket(PacketType.Ping, buf)

                val now = System.currentTimeMillis()
                val sec = maxOf(0.25, (now - lastWindowMs) / 1000.0)
                val fps = framesCount / sec
                val mbps = (bytesCount * 8.0 / 1_000_000.0) / sec
                framesCount = 0
                bytesCount = 0
                lastWindowMs = now
                val w = compositeBitmap?.width ?: 0
                val h = compositeBitmap?.height ?: 0
                onTelemetry?.invoke(RemoteTelemetry(channel?.modeLabel ?: "Relay", fps, lastRttMs, mbps, w, h))
            } catch (_: Exception) {}
            delay(1000)
        }
    }

    private fun initAudioTrack() {
        try {
            val minBuf = AudioTrack.getMinBufferSize(22050, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT)
            audioTrack = AudioTrack.Builder()
                .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
                .setAudioFormat(AudioFormat.Builder().setSampleRate(22050).setEncoding(AudioFormat.ENCODING_PCM_16BIT).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).build())
                .setBufferSizeInBytes(maxOf(minBuf, 4096))
                .setTransferMode(AudioTrack.MODE_STREAM)
                .build().apply { play() }
        } catch (_: Exception) {}
    }

    fun sendMouse(normX: Double, normY: Double, button: Int, action: Int, wheel: Int = 0) {
        scope.launch {
            val buf = ByteBuffer.allocate(20).order(ByteOrder.LITTLE_ENDIAN)
            buf.putDouble(normX.coerceIn(0.0, 1.0))
            buf.putDouble(normY.coerceIn(0.0, 1.0))
            buf.put(button.toByte())
            buf.put(action.toByte())
            buf.putShort(wheel.toShort())
            try { channel?.sendPacket(PacketType.InputMouse, buf.array()) } catch (_: Exception) {}
        }
    }

    fun sendKey(vk: Int, isUp: Boolean, unicode: Char? = null) {
        scope.launch {
            val buf = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN)
            buf.putShort(vk.toShort())
            buf.putShort(0)
            buf.put(if (isUp) 1 else 0)
            buf.put(0)
            buf.putShort((unicode?.code ?: 0).toShort())
            try { channel?.sendPacket(PacketType.InputKeyboard, buf.array()) } catch (_: Exception) {}
        }
    }

    fun selectMonitor(index: Int, quality: Int = 72) {
        scope.launch {
            val buf = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putInt(index).putInt(quality).array()
            try { channel?.sendPacket(PacketType.MonitorSelect, buf) } catch (_: Exception) {}
        }
    }

    fun sendSystemAction(kind: Int) {
        scope.launch {
            try { channel?.sendPacket(PacketType.SessionAction, byteArrayOf(kind.toByte())) } catch (_: Exception) {}
        }
    }

    fun requestFileList(path: String) {
        scope.launch {
            try { channel?.sendPacket(PacketType.FileListReq, path.toByteArray(Charsets.UTF_8)) } catch (_: Exception) {}
        }
    }

    fun requestDownload(remotePath: String, fileName: String) {
        scope.launch {
            val tid = UUID.randomUUID().toString().replace("-", "")
            val json = JSONObject().apply {
                put("TransferId", tid)
                put("IsUploadToRemote", false)
                put("RemotePath", remotePath)
                put("FileName", fileName)
                put("TotalBytes", 0)
                put("Sha256Hex", "")
            }.toString().toByteArray(Charsets.UTF_8)
            try { channel?.sendPacket(PacketType.FileTransferInit, json) } catch (_: Exception) {}
        }
    }

    fun uploadFile(localFile: File, remoteDir: String) {
        scope.launch {
            if (!localFile.exists()) return@launch
            val tid = UUID.randomUUID().toString().replace("-", "")
            val remotePath = if (remoteDir.isBlank()) localFile.name else "$remoteDir\\${localFile.name}"
            val bytes = localFile.readBytes()
            val sha = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02X".format(it) }
            val json = JSONObject().apply {
                put("TransferId", tid)
                put("IsUploadToRemote", true)
                put("RemotePath", remotePath)
                put("FileName", localFile.name)
                put("TotalBytes", bytes.size.toLong())
                put("Sha256Hex", sha)
            }.toString().toByteArray(Charsets.UTF_8)
            channel?.sendPacket(PacketType.FileTransferInit, json)

            val tidBytes = tid.toByteArray(Charsets.US_ASCII)
            var offset = 0
            while (offset < bytes.size) {
                val len = minOf(ProtocolConstants.FILE_CHUNK_BYTES, bytes.size - offset)
                val chunk = ByteArray(36 + len)
                System.arraycopy(tidBytes, 0, chunk, 0, 32)
                ByteBuffer.wrap(chunk, 32, 4).order(ByteOrder.LITTLE_ENDIAN).putInt(len)
                System.arraycopy(bytes, offset, chunk, 36, len)
                channel?.sendPacket(PacketType.FileTransferChunk, chunk)
                offset += len
            }
            onTransferStatus?.invoke("Uploaded ${localFile.name}")
        }
    }

    fun sendChat(text: String) {
        scope.launch {
            val json = JSONObject().apply {
                put("SenderName", android.os.Build.MODEL)
                put("Text", text)
                put("TimestampUtc", java.time.Instant.now().toString())
            }.toString().toByteArray(Charsets.UTF_8)
            try { channel?.sendPacket(PacketType.ChatMessage, json) } catch (_: Exception) {}
        }
    }

    fun setAudioEnabled(enabled: Boolean) {
        audioEnabled = enabled
    }

    fun disconnect() {
        try {
            activeDownloads.values.forEach { runCatching { it.first.close() } }
            activeDownloads.clear()
            audioTrack?.stop()
            audioTrack?.release()
            audioTrack = null
            channel?.close()
            channel = null
        } catch (_: Exception) {}
    }

    fun destroy() {
        disconnect()
        scope.cancel()
    }
}
