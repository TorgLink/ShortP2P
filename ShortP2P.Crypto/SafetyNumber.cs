using System.Security.Cryptography;

namespace ShortP2P.Crypto;

/// <summary>
/// Deterministic 4-emoji safety number from an identity public key (SHA-256 of Ed25519||X25519).
/// </summary>
public static class SafetyNumber
{
    public const int EmojiCount = 4;

    /// <summary>256 distinct pictographs (U+1F400–U+1F4FF), indexed by a hash byte.</summary>
    public static readonly string[] Glyphs = BuildGlyphs();

    public static string FromPublicKey(IdentityPublicKey key)
    {
        Require.NotNull(key);
#if NETFRAMEWORK
        var hash = NetFrameworkCrypto.Sha256(CryptoPrimitives.Concat(key.Ed25519Public, key.X25519Public));
#else
        var hash = SHA256.HashData(CryptoPrimitives.Concat(key.Ed25519Public, key.X25519Public));
#endif
        return $"{Glyphs[hash[0]]}{Glyphs[hash[1]]}{Glyphs[hash[2]]}{Glyphs[hash[3]]}";
    }

    public static bool TryFromPublicKeyJson(string? json, out string emojis)
    {
        emojis = "";
        if (json is null || string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            emojis = FromPublicKey(IdentityKeySerializer.DeserializePublic(json));
            return emojis.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static string FromPublicKeyJsonOrEmpty(string? json) =>
        TryFromPublicKeyJson(json, out var emojis) ? emojis : "";

    public static string FormatPair(string myNick, string myEmojis, string peerNick, string peerEmojis) =>
        $"{(myNick ?? "").Trim()}: {myEmojis}, {(peerNick ?? "").Trim()}: {peerEmojis}";

    public static bool PublicKeyJsonEquals(string? a, string? b) =>
        IdentityKeySerializer.PublicKeyJsonEquals(a, b);

    private static string[] BuildGlyphs()
    {
        var glyphs = new string[256];
        for (var i = 0; i < 256; i++)
            glyphs[i] = char.ConvertFromUtf32(0x1F400 + i);
        return glyphs;
    }
}
