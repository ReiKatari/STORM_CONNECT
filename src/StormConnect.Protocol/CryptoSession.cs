using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace StormConnect.Protocol;

/// <summary>
/// Wire-compatible ECDH P-256 + HKDF-SHA256 + AES-256-GCM end-to-end encrypted session.
/// Uses uncompressed P-256 EC point format (0x04 || X(32) || Y(32), 65 bytes) so .NET and Android/Java interop cleanly.
/// </summary>
public sealed class CryptoSession : IDisposable
{
    private ECDiffieHellman? _ecdh;
    private byte[]? _txKey;
    private byte[]? _rxKey;
    private long _txCounter;
    private long _rxMaxSeq;
    private ulong _rxWindowMask;
    private readonly byte[] _txSaltPrefix = new byte[4];
    private readonly byte[] _rxSaltPrefix = new byte[4];
    private readonly object _txLock = new();
    private readonly object _rxLock = new();

    public bool IsEstablished => _txKey is not null && _rxKey is not null;
    public byte[] LocalPublicKeyUncompressed { get; }

    public CryptoSession()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = _ecdh.ExportParameters(false);
        LocalPublicKeyUncompressed = new byte[65];
        LocalPublicKeyUncompressed[0] = 0x04;
        Buffer.BlockCopy(p.Q.X!, 0, LocalPublicKeyUncompressed, 1, 32);
        Buffer.BlockCopy(p.Q.Y!, 0, LocalPublicKeyUncompressed, 33, 32);
    }

    public void DeriveKeys(byte[] peerPublicKeyUncompressed, bool isInitiator)
    {
        if (_ecdh is null) throw new ObjectDisposedException(nameof(CryptoSession));
        if (peerPublicKeyUncompressed.Length != 65 || peerPublicKeyUncompressed[0] != 0x04)
            throw new CryptographicException("Invalid P-256 uncompressed public key.");

        var peerParams = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = peerPublicKeyUncompressed.AsSpan(1, 32).ToArray(),
                Y = peerPublicKeyUncompressed.AsSpan(33, 32).ToArray()
            }
        };

        using var peerEcdh = ECDiffieHellman.Create(peerParams);
        var sharedSecret = _ecdh.DeriveRawSecretAgreement(peerEcdh.PublicKey);

        // Deterministic directional key derivation via HKDF-SHA256
        var infoC2H = Encoding.ASCII.GetBytes("STORM-CONNECT-C2H-KEY-V1");
        var infoH2C = Encoding.ASCII.GetBytes("STORM-CONNECT-H2C-KEY-V1");
        var salt = Encoding.ASCII.GetBytes("STORM-CONNECT-HKDF-SALT-V1");

        var keyC2H = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, salt, infoC2H);
        var keyH2C = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, salt, infoH2C);

        _txKey = isInitiator ? keyC2H : keyH2C;
        _rxKey = isInitiator ? keyH2C : keyC2H;

        BinaryPrimitives.WriteUInt32BigEndian(_txSaltPrefix, isInitiator ? 0x43324801u : 0x48324301u);
        BinaryPrimitives.WriteUInt32BigEndian(_rxSaltPrefix, isInitiator ? 0x48324301u : 0x43324801u);
        Array.Clear(sharedSecret);
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        if (_txKey is null) throw new InvalidOperationException("Session keys not derived.");
        lock (_txLock)
        {
            long seq = Interlocked.Increment(ref _txCounter);
            Span<byte> nonce = stackalloc byte[12];
            _txSaltPrefix.CopyTo(nonce);
            BinaryPrimitives.WriteInt64BigEndian(nonce[4..], seq);

            // Output layout: [8 bytes seq][16 bytes tag][ciphertext]
            var output = new byte[8 + 16 + plaintext.Length];
            BinaryPrimitives.WriteInt64BigEndian(output.AsSpan(0, 8), seq);
            using var aes = new AesGcm(_txKey, 16);
            aes.Encrypt(nonce, plaintext, output.AsSpan(24), output.AsSpan(8, 16));
            return output;
        }
    }

    public byte[] Decrypt(ReadOnlySpan<byte> encryptedFrame)
    {
        if (_rxKey is null) throw new InvalidOperationException("Session keys not derived.");
        if (encryptedFrame.Length < 24) throw new CryptographicException("Encrypted frame too short.");

        lock (_rxLock)
        {
            long seq = BinaryPrimitives.ReadInt64BigEndian(encryptedFrame[..8]);
            if (seq <= 0)
                throw new CryptographicException("Invalid encrypted sequence number.");

            if (seq <= _rxMaxSeq)
            {
                long diff = _rxMaxSeq - seq;
                if (diff >= 64 || (_rxWindowMask & (1UL << (int)diff)) != 0)
                    throw new CryptographicException("Replay or out-of-window sequence detected.");
            }

            Span<byte> nonce = stackalloc byte[12];
            _rxSaltPrefix.CopyTo(nonce);
            BinaryPrimitives.WriteInt64BigEndian(nonce[4..], seq);

            var tag = encryptedFrame.Slice(8, 16);
            var ciphertext = encryptedFrame[24..];
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(_rxKey, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            if (seq > _rxMaxSeq)
            {
                long shift = seq - _rxMaxSeq;
                _rxWindowMask = shift >= 64 ? 1UL : ((_rxWindowMask << (int)shift) | 1UL);
                _rxMaxSeq = seq;
            }
            else
            {
                long diff = _rxMaxSeq - seq;
                _rxWindowMask |= (1UL << (int)diff);
            }
            return plaintext;
        }
    }

    public static string HashPassword(string password, byte[] salt)
    {
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            100_000,
            HashAlgorithmName.SHA256,
            32);
        return Convert.ToBase64String(hash);
    }

    public static string ComputeChallengeProof(string passwordOrPin, byte[] salt, byte[] challengeNonce)
    {
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passwordOrPin),
            salt,
            100_000,
            HashAlgorithmName.SHA256,
            32);
        using var hmac = new HMACSHA256(derived);
        return Convert.ToBase64String(hmac.ComputeHash(challengeNonce));
    }

    public static bool VerifyChallengeProof(string storedHashBase64, byte[] challengeNonce, string? providedProofBase64)
    {
        if (string.IsNullOrWhiteSpace(providedProofBase64)) return false;
        try
        {
            var key = Convert.FromBase64String(storedHashBase64);
            var provided = Convert.FromBase64String(providedProofBase64);
            using var hmac = new HMACSHA256(key);
            var expected = hmac.ComputeHash(challengeNonce);
            return CryptographicOperations.FixedTimeEquals(expected, provided);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _ecdh?.Dispose();
        _ecdh = null;
        if (_txKey is not null) Array.Clear(_txKey);
        if (_rxKey is not null) Array.Clear(_rxKey);
    }
}
