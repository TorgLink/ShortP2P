using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace ShortP2P.Crypto;

/// <summary>
///     V2 handshake: ephemeral X25519 ECDH against the peer's static X25519 key,
///     then AES-GCM wrap of (aesKey||macKey). Packet is fixed-length for LoRa-friendly caching.
/// </summary>
public static class P2PHandshake
{
    public const byte PacketVersion = 2;
    private const int SessionKeyBytes = 16 + 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>ver(1) + eph_x25519(32) + nonce(12) + ciphertext(48) + tag(16).</summary>
    public const int HandshakePacketBytes =
        1 + IdentityPublicKey.X25519PublicKeyLength + NonceBytes + SessionKeyBytes + TagBytes;

    private static ReadOnlySpan<byte> HkdfInfo => "ShortP2P handshake v2"u8;

    public static P2PHandshakeResult CreateHandshakeInitiation(IdentityPublicKey remotePublicKey)
    {
        Require.NotNull(remotePublicKey);

        var sessionKeys = new byte[SessionKeyBytes];
#if NETFRAMEWORK
        NetFrameworkCrypto.FillRandom(sessionKeys);
#else
        RandomNumberGenerator.Fill(sessionKeys);
#endif

        var random = new SecureRandom();
        var ephGen = new X25519KeyPairGenerator();
        ephGen.Init(new X25519KeyGenerationParameters(random));
        var ephKp = ephGen.GenerateKeyPair();
        var ephPriv = (X25519PrivateKeyParameters)ephKp.Private;
        var ephPub = (X25519PublicKeyParameters)ephKp.Public;

        var peerStatic = new X25519PublicKeyParameters(remotePublicKey.X25519Public, 0);
        var shared = Agree(ephPriv, peerStatic);
        var wrapKey = DeriveWrapKey(shared, ephPub.GetEncoded(), remotePublicKey.X25519Public);

        var nonce = new byte[NonceBytes];
#if NETFRAMEWORK
        NetFrameworkCrypto.FillRandom(nonce);
#else
        RandomNumberGenerator.Fill(nonce);
#endif
        var ciphertext = new byte[SessionKeyBytes];
        var tag = new byte[TagBytes];
#if NETFRAMEWORK
        NetFrameworkCrypto.AesGcmEncrypt(wrapKey, nonce, sessionKeys, ciphertext, tag);
#else
        using (var aes = new AesGcm(wrapKey, TagBytes))
            aes.Encrypt(nonce, sessionKeys, ciphertext, tag);
#endif

        var packet = new byte[HandshakePacketBytes];
        packet[0] = PacketVersion;
        ephPub.GetEncoded().CopyTo(packet.AsSpan(1, IdentityPublicKey.X25519PublicKeyLength));
        nonce.CopyTo(packet.AsSpan(1 + IdentityPublicKey.X25519PublicKeyLength, NonceBytes));
        ciphertext.CopyTo(packet.AsSpan(1 + IdentityPublicKey.X25519PublicKeyLength + NonceBytes, SessionKeyBytes));
        tag.CopyTo(packet.AsSpan(
            1 + IdentityPublicKey.X25519PublicKeyLength + NonceBytes + SessionKeyBytes, TagBytes));

        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(wrapKey);

        var aesKey = new byte[16];
        var macKey = new byte[32];
        Buffer.BlockCopy(sessionKeys, 0, aesKey, 0, 16);
        Buffer.BlockCopy(sessionKeys, 16, macKey, 0, 32);
        CryptographicOperations.ZeroMemory(sessionKeys);

        return new P2PHandshakeResult(packet, new P2PSession(aesKey, macKey));
    }

    public static byte[] CreateHandshakePacket(IdentityPublicKey remotePublicKey) =>
        CreateHandshakeInitiation(remotePublicKey).HandshakePacket;

    public static byte[] DecryptHandshakePacket(IdentityPrivateKey localPrivateKey, byte[] handshakePacket)
    {
        Require.NotNull(localPrivateKey);
        Require.NotNull(handshakePacket);
        if (handshakePacket.Length != HandshakePacketBytes)
            throw new ArgumentException($"Handshake packet must be exactly {HandshakePacketBytes} bytes.",
                nameof(handshakePacket));
        if (handshakePacket[0] != PacketVersion)
            throw new CryptographicException($"Unsupported handshake packet version {handshakePacket[0]}.");

        var o = 1;
        var ephPubBytes = handshakePacket.AsSpan(o, IdentityPublicKey.X25519PublicKeyLength).ToArray();
        o += IdentityPublicKey.X25519PublicKeyLength;
        var nonce = handshakePacket.AsSpan(o, NonceBytes);
        o += NonceBytes;
        var ciphertext = handshakePacket.AsSpan(o, SessionKeyBytes);
        o += SessionKeyBytes;
        var tag = handshakePacket.AsSpan(o, TagBytes);

        var ephPub = new X25519PublicKeyParameters(ephPubBytes, 0);
        var localStatic = new X25519PrivateKeyParameters(localPrivateKey.X25519Private, 0);
        var shared = Agree(localStatic, ephPub);
        var wrapKey = DeriveWrapKey(shared, ephPubBytes, localPrivateKey.X25519Public);

        var sessionKeys = new byte[SessionKeyBytes];
        try
        {
#if NETFRAMEWORK
            NetFrameworkCrypto.AesGcmDecrypt(wrapKey, nonce.ToArray(), ciphertext.ToArray(), tag.ToArray(),
                sessionKeys);
#else
            using var aes = new AesGcm(wrapKey, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, sessionKeys);
#endif
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
            CryptographicOperations.ZeroMemory(wrapKey);
        }

        return sessionKeys;
    }

    private static byte[] Agree(X25519PrivateKeyParameters privateKey, X25519PublicKeyParameters publicKey)
    {
        var agreement = new X25519Agreement();
        agreement.Init(privateKey);
        var shared = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(publicKey, shared, 0);
        return shared;
    }

    private static byte[] DeriveWrapKey(byte[] sharedSecret, byte[] ephemeralPublic, byte[] recipientStaticPublic)
    {
        // salt = eph_pub || recipient_static_pub (order fixed for both sides)
        var salt = new byte[ephemeralPublic.Length + recipientStaticPublic.Length];
        ephemeralPublic.CopyTo(salt, 0);
        recipientStaticPublic.CopyTo(salt, ephemeralPublic.Length);
#if NETFRAMEWORK
        return NetFrameworkCrypto.HkdfSha256(sharedSecret, 32, salt, HkdfInfo.ToArray());
#else
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, salt, HkdfInfo.ToArray());
#endif
    }
}
