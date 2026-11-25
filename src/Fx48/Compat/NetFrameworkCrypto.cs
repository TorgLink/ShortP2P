#if NETFRAMEWORK
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace ShortP2P.Crypto;

/// <summary>Polyfills for APIs missing on .NET Framework 4.7.2.</summary>
internal static class NetFrameworkCrypto
{
    public static byte[] Sha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }

    public static void FillRandom(byte[] buffer)
    {
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(buffer);
    }

    public static byte[] HkdfSha256(byte[] ikm, int length, byte[] salt, byte[] info)
    {
        // HKDF-Extract
        using var hmac = new HMACSHA256(salt.Length == 0 ? new byte[32] : salt);
        var prk = hmac.ComputeHash(ikm);

        // HKDF-Expand
        var result = new byte[length];
        var t = Array.Empty<byte>();
        var offset = 0;
        byte counter = 1;
        while (offset < length)
        {
            using var expand = new HMACSHA256(prk);
            expand.TransformBlock(t, 0, t.Length, null, 0);
            expand.TransformBlock(info, 0, info.Length, null, 0);
            expand.TransformFinalBlock(new[] { counter }, 0, 1);
            t = expand.Hash ?? throw new CryptographicException("HKDF expand failed.");
            var take = Math.Min(t.Length, length - offset);
            Buffer.BlockCopy(t, 0, result, offset, take);
            offset += take;
            counter++;
        }

        CryptographicOperations.ZeroMemory(prk);
        return result;
    }

    public static void AesGcmEncrypt(byte[] key, byte[] nonce, byte[] plaintext, byte[] ciphertext, byte[] tag)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(new KeyParameter(key), tag.Length * 8, nonce));
        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        len += cipher.DoFinal(output, len);
        if (len != ciphertext.Length + tag.Length)
            throw new CryptographicException("Unexpected AES-GCM encrypt length.");
        Buffer.BlockCopy(output, 0, ciphertext, 0, ciphertext.Length);
        Buffer.BlockCopy(output, ciphertext.Length, tag, 0, tag.Length);
    }

    public static void AesGcmDecrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag, byte[] plaintext)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(false, new AeadParameters(new KeyParameter(key), tag.Length * 8, nonce));
        var input = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, input, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, input, ciphertext.Length, tag.Length);
        var output = new byte[cipher.GetOutputSize(input.Length)];
        var len = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        len += cipher.DoFinal(output, len);
        if (len != plaintext.Length)
            throw new CryptographicException("Unexpected AES-GCM decrypt length.");
        Buffer.BlockCopy(output, 0, plaintext, 0, plaintext.Length);
    }
}
#endif
