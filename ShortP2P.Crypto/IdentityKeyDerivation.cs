using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace ShortP2P.Crypto;

/// <summary>Creates Ed25519/X25519 identity pairs and binds them to <c>NetworkId</c>.</summary>
public static class IdentityKeyDerivation
{
    public static IdentityKeyPair GenerateKeyPair()
    {
        var random = new SecureRandom();

        var edGen = new Ed25519KeyPairGenerator();
        edGen.Init(new Ed25519KeyGenerationParameters(random));
        var edKp = edGen.GenerateKeyPair();
        var edPriv = (Ed25519PrivateKeyParameters)edKp.Private;
        var edPub = (Ed25519PublicKeyParameters)edKp.Public;

        var xGen = new X25519KeyPairGenerator();
        xGen.Init(new X25519KeyGenerationParameters(random));
        var xKp = xGen.GenerateKeyPair();
        var xPriv = (X25519PrivateKeyParameters)xKp.Private;
        var xPub = (X25519PublicKeyParameters)xKp.Public;

        var publicKey = new IdentityPublicKey(edPub.GetEncoded(), xPub.GetEncoded());
        var privateKey = new IdentityPrivateKey(edPriv.GetEncoded(), edPub.GetEncoded(), xPriv.GetEncoded(),
            xPub.GetEncoded());
        return new IdentityKeyPair(publicKey, privateKey);
    }

    /// <summary>
    ///     NetworkId = first 12 bytes of SHA-256(Ed25519 public key).
    /// </summary>
    public static byte[] NetworkIdWireFromPublicKey(IdentityPublicKey publicKey)
    {
        Require.NotNull(publicKey);
#if NETFRAMEWORK
        var hash = NetFrameworkCrypto.Sha256(publicKey.Ed25519Public);
#else
        var hash = SHA256.HashData(publicKey.Ed25519Public);
#endif
        return hash.AsSpan(0, 12).ToArray();
    }

    public static bool NetworkIdMatchesPublicKey(ReadOnlySpan<byte> networkIdWire, IdentityPublicKey publicKey)
    {
        if (networkIdWire.Length != 12)
            return false;
        var expected = NetworkIdWireFromPublicKey(publicKey);
        return CryptographicOperations.FixedTimeEquals(networkIdWire, expected);
    }
}
