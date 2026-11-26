using ShortP2P.Crypto;

namespace ShortP2P.Crypto.Tests;

public class SafetyNumberTests
{
    [Fact]
    public void Glyphs_Are256Distinct()
    {
        Assert.Equal(256, SafetyNumber.Glyphs.Length);
        Assert.Equal(256, SafetyNumber.Glyphs.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void FromPublicKey_IsDeterministicFourEmojis()
    {
        var kp = P2PCrypto.GenerateKeyPair();
        var a = SafetyNumber.FromPublicKey(kp.PublicKey);
        var b = SafetyNumber.FromPublicKey(kp.PublicKey);
        Assert.Equal(a, b);
        Assert.Equal(4, a.EnumerateRunes().Count());
    }

    [Fact]
    public void FromPublicKey_DiffersForDifferentKeys()
    {
        var a = P2PCrypto.GenerateKeyPair();
        var b = P2PCrypto.GenerateKeyPair();
        Assert.NotEqual(
            SafetyNumber.FromPublicKey(a.PublicKey),
            SafetyNumber.FromPublicKey(b.PublicKey));
    }

    [Fact]
    public void FromPublicKeyJson_IgnoresJsonWhitespace()
    {
        var kp = P2PCrypto.GenerateKeyPair();
        var json = IdentityKeySerializer.SerializePublic(kp.PublicKey);
        Assert.True(SafetyNumber.TryFromPublicKeyJson(json, out var emojis));
        Assert.Equal(emojis, SafetyNumber.FromPublicKey(kp.PublicKey));
        Assert.True(SafetyNumber.PublicKeyJsonEquals(json, " " + json + " "));
    }

    [Fact]
    public void FormatPair_IncludesNicksAndEmojis()
    {
        var text = SafetyNumber.FormatPair("Alice", "🐀🐄", "Bob", "🐅🐇");
        Assert.Equal("Alice: 🐀🐄, Bob: 🐅🐇", text);
    }
}
