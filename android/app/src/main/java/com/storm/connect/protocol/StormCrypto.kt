package com.storm.connect.protocol

import java.math.BigInteger
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.AlgorithmParameters
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPublicKeySpec
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.PBEKeySpec
import javax.crypto.spec.SecretKeySpec

/**
 * Wire-compatible ECDH P-256 + HKDF-SHA256 + AES-256-GCM encryption
 * matching StormConnect.Protocol.CryptoSession on .NET 10.
 */
class StormCrypto {
    private val keyPair = KeyPairGenerator.getInstance("EC").apply {
        initialize(ECGenParameterSpec("secp256r1"))
    }.generateKeyPair()

    private val ecSpec: ECParameterSpec = (keyPair.public as ECPublicKey).params
    private var txKey: ByteArray? = null
    private var rxKey: ByteArray? = null
    private var txCounter: Long = 0
    private var rxMaxSeq: Long = 0
    private var rxWindowMask: Long = 0L
    private var txSaltPrefix = ByteArray(4)
    private var rxSaltPrefix = ByteArray(4)
    private val txLock = Any()
    private val rxLock = Any()

    val localPublicKeyUncompressed: ByteArray by lazy {
        val pub = keyPair.public as ECPublicKey
        val x = toFixed32(pub.w.affineX)
        val y = toFixed32(pub.w.affineY)
        ByteArray(65).apply {
            this[0] = 0x04
            System.arraycopy(x, 0, this, 1, 32)
            System.arraycopy(y, 0, this, 33, 32)
        }
    }

    fun deriveKeys(peerPublicKeyUncompressed: ByteArray, isInitiator: Boolean) {
        require(peerPublicKeyUncompressed.size == 65 && peerPublicKeyUncompressed[0] == 0x04.toByte()) {
            "Invalid P-256 uncompressed public key"
        }
        val x = BigInteger(1, peerPublicKeyUncompressed.copyOfRange(1, 33))
        val y = BigInteger(1, peerPublicKeyUncompressed.copyOfRange(33, 65))
        val pubSpec = ECPublicKeySpec(ECPoint(x, y), ecSpec)
        val peerPub = KeyFactory.getInstance("EC").generatePublic(pubSpec)

        val ka = KeyAgreement.getInstance("ECDH")
        ka.init(keyPair.private)
        ka.doPhase(peerPub, true)
        val sharedSecret = ka.generateSecret()

        val salt = "STORM-CONNECT-HKDF-SALT-V1".toByteArray(Charsets.US_ASCII)
        val keyC2H = hkdfSha256(sharedSecret, salt, "STORM-CONNECT-C2H-KEY-V1".toByteArray(Charsets.US_ASCII), 32)
        val keyH2C = hkdfSha256(sharedSecret, salt, "STORM-CONNECT-H2C-KEY-V1".toByteArray(Charsets.US_ASCII), 32)

        txKey = if (isInitiator) keyC2H else keyH2C
        rxKey = if (isInitiator) keyH2C else keyC2H

        val c2hPrefix = 0x43324801
        val h2cPrefix = 0x48324301
        ByteBuffer.wrap(txSaltPrefix).order(ByteOrder.BIG_ENDIAN).putInt(if (isInitiator) c2hPrefix else h2cPrefix)
        ByteBuffer.wrap(rxSaltPrefix).order(ByteOrder.BIG_ENDIAN).putInt(if (isInitiator) h2cPrefix else c2hPrefix)
        sharedSecret.fill(0)
    }

    fun encrypt(plaintext: ByteArray): ByteArray = synchronized(txLock) {
        val key = txKey ?: error("Keys not derived")
        val seq = ++txCounter
        val nonce = ByteArray(12)
        System.arraycopy(txSaltPrefix, 0, nonce, 0, 4)
        ByteBuffer.wrap(nonce, 4, 8).order(ByteOrder.BIG_ENDIAN).putLong(seq)

        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
        val ctAndTag = cipher.doFinal(plaintext) // [ciphertext][16B tag]
        val ctLen = ctAndTag.size - 16

        // Wire layout matches .NET: [8B seq][16B tag][ciphertext]
        return ByteArray(8 + 16 + ctLen).apply {
            ByteBuffer.wrap(this, 0, 8).order(ByteOrder.BIG_ENDIAN).putLong(seq)
            System.arraycopy(ctAndTag, ctLen, this, 8, 16)
            System.arraycopy(ctAndTag, 0, this, 24, ctLen)
        }
    }

    fun decrypt(frame: ByteArray): ByteArray = synchronized(rxLock) {
        val key = rxKey ?: error("Keys not derived")
        require(frame.size >= 24) { "Encrypted frame too short" }

        val seq = ByteBuffer.wrap(frame, 0, 8).order(ByteOrder.BIG_ENDIAN).long
        require(seq > 0) { "Invalid sequence" }
        if (seq <= rxMaxSeq) {
            val diff = rxMaxSeq - seq
            require(diff < 64 && (rxWindowMask and (1L shl diff.toInt())) == 0L) { "Replay detected" }
        }

        val nonce = ByteArray(12)
        System.arraycopy(rxSaltPrefix, 0, nonce, 0, 4)
        ByteBuffer.wrap(nonce, 4, 8).order(ByteOrder.BIG_ENDIAN).putLong(seq)

        val ctLen = frame.size - 24
        val ctAndTag = ByteArray(ctLen + 16)
        System.arraycopy(frame, 24, ctAndTag, 0, ctLen)
        System.arraycopy(frame, 8, ctAndTag, ctLen, 16)

        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
        val plain = cipher.doFinal(ctAndTag)
        if (seq > rxMaxSeq) {
            val shift = seq - rxMaxSeq
            rxWindowMask = if (shift >= 64) 1L else ((rxWindowMask shl shift.toInt()) or 1L)
            rxMaxSeq = seq
        } else {
            val diff = (rxMaxSeq - seq).toInt()
            rxWindowMask = rxWindowMask or (1L shl diff)
        }
        return plain
    }

    companion object {
        private fun toFixed32(bi: BigInteger): ByteArray {
            val raw = bi.toByteArray()
            return when {
                raw.size == 32 -> raw
                raw.size > 32 -> raw.copyOfRange(raw.size - 32, raw.size)
                else -> ByteArray(32).apply { System.arraycopy(raw, 0, this, 32 - raw.size, raw.size) }
            }
        }

        private fun hkdfSha256(ikm: ByteArray, salt: ByteArray, info: ByteArray, length: Int): ByteArray {
            val mac = Mac.getInstance("HmacSHA256")
            mac.init(SecretKeySpec(salt, "HmacSHA256"))
            val prk = mac.doFinal(ikm)

            val okm = ByteArray(length)
            var t = ByteArray(0)
            var offset = 0
            var counter = 1
            while (offset < length) {
                mac.init(SecretKeySpec(prk, "HmacSHA256"))
                mac.update(t)
                mac.update(info)
                mac.update(counter.toByte())
                t = mac.doFinal()
                val copyLen = minOf(t.size, length - offset)
                System.arraycopy(t, 0, okm, offset, copyLen)
                offset += copyLen
                counter++
            }
            return okm
        }

        fun hashPassword(password: String, salt: ByteArray): String {
            val spec = PBEKeySpec(password.toCharArray(), salt, 100_000, 256)
            val skf = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256")
            val key = skf.generateSecret(spec).encoded
            return Base64.getEncoder().encodeToString(key)
        }

        fun computeChallengeProof(passwordOrPin: String, salt: ByteArray, nonce: ByteArray): String {
            val spec = PBEKeySpec(passwordOrPin.toCharArray(), salt, 100_000, 256)
            val skf = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256")
            val derived = skf.generateSecret(spec).encoded
            val mac = Mac.getInstance("HmacSHA256")
            mac.init(SecretKeySpec(derived, "HmacSHA256"))
            return Base64.getEncoder().encodeToString(mac.doFinal(nonce))
        }

        fun verifyChallengeProof(storedHashBase64: String, nonce: ByteArray, proofBase64: String?): Boolean {
            if (proofBase64.isNullOrBlank()) return false
            return try {
                val key = Base64.getDecoder().decode(storedHashBase64)
                val provided = Base64.getDecoder().decode(proofBase64)
                val mac = Mac.getInstance("HmacSHA256")
                mac.init(SecretKeySpec(key, "HmacSHA256"))
                val expected = mac.doFinal(nonce)
                MessageDigest.isEqual(expected, provided)
            } catch (_: Exception) {
                false
            }
        }
    }
}
