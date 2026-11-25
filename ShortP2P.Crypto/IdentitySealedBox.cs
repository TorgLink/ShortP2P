using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace ShortP2P.Crypto;

/// <summary>
///     Encrypt a small blob to a peer's static X25519 key (ephemeral ECDH + HKDF + AES-GCM).
/// </summary>
public static class IdentitySealedBox
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private static ReadOnlySpan<byte> HkdfInfo => "ShortP2P sealed-box v2"u8;

    public static byte[] Seal(IdentityPublicKey recipient, ReadOnlySpan<byte> plaintext)
    {
        Require.NotNull(recipient);

        var random = new SecureRandom();
        var ephGen = new X25519KeyPairGenerator();
        ephGen.Init(new X25519KeyGenerationParameters(random));
        var ephKp = ephGen.GenerateKeyPair();
        var ephPriv = (X25519PrivateKeyParameters)ephKp.Private;
        var ephPub = (X25519PublicKeyParameters)ephKp.Public;
        var ephPubBytes = ephPub.GetEncoded();

        var peerStatic = new X25519PublicKeyParameters(recipient.X25519Public, 0);
        var shared = Agree(ephPriv, peerStatic);
        var key = DeriveKey(shared, ephPubBytes, recipient.X25519Public);

        var nonce = new byte[NonceBytes];
#if NETFRAMEWORK
        NetFrameworkCrypto.FillRandom(nonce);
#else
        RandomNumberGenerator.Fill(nonce);
#endif
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
#if NETFRAMEWORK
        NetFrameworkCrypto.AesGcmEncrypt(key, nonce, plaintext.ToArray(), ciphertext, tag);
#else
        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
#endif

        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(key);

        var result = new byte[IdentityPublicKey.X25519PublicKeyLength + NonceBytes + ciphertext.Length + TagBytes];
        ephPubBytes.CopyTo(result.AsSpan(0, IdentityPublicKey.X25519PublicKeyLength));
        nonce.CopyTo(result.AsSpan(IdentityPublicKey.X25519PublicKeyLength, NonceBytes));
        ciphertext.CopyTo(result.AsSpan(IdentityPublicKey.X25519PublicKeyLength + NonceBytes, ciphertext.Length));
        tag.CopyTo(result.AsSpan(result.Length - TagBytes, TagBytes));
        return result;
    }

    public static byte[] Open(IdentityPrivateKey recipient, ReadOnlySpan<byte> sealedBox)
    {
        Require.NotNull(recipient);
        var min = IdentityPublicKey.X25519PublicKeyLength + NonceBytes + TagBytes;
        if (sealedBox.Length < min)
            throw new CryptographicException("Sealed box is too short.");

        var ephPubBytes = sealedBox[..IdentityPublicKey.X25519PublicKeyLength].ToArray();
        var nonce = sealedBox.Slice(IdentityPublicKey.X25519PublicKeyLength, NonceBytes);
        var cipherLen = sealedBox.Length - IdentityPublicKey.X25519PublicKeyLength - NonceBytes - TagBytes;
        var ciphertext = sealedBox.Slice(IdentityPublicKey.X25519PublicKeyLength + NonceBytes, cipherLen);
        var tag = sealedBox[^TagBytes..];

        var ephPub = new X25519PublicKeyParameters(ephPubBytes, 0);
        var localStatic = new X25519PrivateKeyParameters(recipient.X25519Private, 0);
        var shared = Agree(localStatic, ephPub);
        var key = DeriveKey(shared, ephPubBytes, recipient.X25519Public);

        var plaintext = new byte[cipherLen];
        try
        {
#if NETFRAMEWORK
            NetFrameworkCrypto.AesGcmDecrypt(key, nonce.ToArray(), ciphertext.ToArray(), tag.ToArray(), plaintext);
#else
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
#endif
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
            CryptographicOperations.ZeroMemory(key);
        }

        return plaintext;
    }

    private static byte[] Agree(X25519PrivateKeyParameters privateKey, X25519PublicKeyParameters publicKey)
    {
        var agreement = new X25519Agreement();
        agreement.Init(privateKey);
        var shared = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(publicKey, shared, 0);
        return shared;
    }

    private static byte[] DeriveKey(byte[] sharedSecret, byte[] ephemeralPublic, byte[] recipientStaticPublic)
    {
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
