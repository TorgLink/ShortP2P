using System.Security.Cryptography;

namespace ShortP2P.Crypto;

/// <summary>
///     Public facade for P2P identity key generation, handshake, and packet encryption.
/// </summary>
public static class P2PCrypto
{
    /// <summary>Generates an Ed25519 + X25519 identity key pair.</summary>
    public static IdentityKeyPair GenerateKeyPair() => IdentityKeyDerivation.GenerateKeyPair();

    /// <summary>Creates a handshake packet for the peer (caller provides the peer's public identity).</summary>
    public static byte[] CreateHandshake(IdentityPublicKey remotePublicKey)
    {
        return P2PHandshake.CreateHandshakePacket(remotePublicKey);
    }

    /// <summary>
    ///     Starts the handshake from the initiator side: returns the packet to send to the peer
    ///     and a ready-to-use session for encrypting packets.
    /// </summary>
    public static P2PHandshakeResult CreateHandshakeInitiation(IdentityPublicKey remotePublicKey)
    {
        return P2PHandshake.CreateHandshakeInitiation(remotePublicKey);
    }

    /// <summary>
    ///     Creates a session from peer handshake packet by decrypting it with the local private identity key.
    /// </summary>
    public static P2PSession CreateSession(IdentityPrivateKey localPrivateKey, byte[] remoteHandshakePacket)
    {
        Require.NotNull(localPrivateKey);
        Require.NotNull(remoteHandshakePacket);

        var sessionKeys = P2PHandshake.DecryptHandshakePacket(localPrivateKey, remoteHandshakePacket);

        var aesKey = new byte[16];
        var macKey = new byte[32];
        Buffer.BlockCopy(sessionKeys, 0, aesKey, 0, 16);
        Buffer.BlockCopy(sessionKeys, 16, macKey, 0, 32);
        CryptographicOperations.ZeroMemory(sessionKeys);

        return new P2PSession(aesKey, macKey);
    }

    /// <summary>Convenience wrapper for <see cref="P2PSession.Encrypt(byte[])" />.</summary>
    public static byte[] Encrypt(P2PSession session, byte[] plaintext)
    {
        Require.NotNull(session);
        return session.Encrypt(plaintext);
    }

    /// <summary>Convenience wrapper for <see cref="P2PSession.Decrypt(byte[])" />.</summary>
    public static byte[] Decrypt(P2PSession session, byte[] packet)
    {
        Require.NotNull(session);
        return session.Decrypt(packet);
    }
}
