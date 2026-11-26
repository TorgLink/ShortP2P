using System.Text.Json;

namespace ShortP2P.Crypto;

/// <summary>JSON wire/storage for v2 identity keys (<c>v:2</c>).</summary>
public static class IdentityKeySerializer
{
    public const int FormatVersion = 2;

    public static string SerializePublic(IdentityPublicKey key)
    {
        Require.NotNull(key);
        var dto = new PublicDto
        {
            V = FormatVersion,
            Ed = Convert.ToBase64String(key.Ed25519Public),
            X = Convert.ToBase64String(key.X25519Public)
        };
        return JsonSerializer.Serialize(dto);
    }

    public static IdentityPublicKey DeserializePublic(string json)
    {
        Require.NotNull(json);
        var dto = JsonSerializer.Deserialize<PublicDto>(json)
                  ?? throw new FormatException("Invalid identity public key JSON.");
        if (dto.V != FormatVersion)
            throw new FormatException($"Unsupported identity public key version {dto.V}.");
        return new IdentityPublicKey(Convert.FromBase64String(dto.Ed), Convert.FromBase64String(dto.X));
    }

    public static bool TryDeserializePublic(string? json, out IdentityPublicKey key)
    {
        key = null!;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            key = DeserializePublic(json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string SerializePrivate(IdentityPrivateKey key)
    {
        Require.NotNull(key);
        var dto = new PrivateDto
        {
            V = FormatVersion,
            Ed = Convert.ToBase64String(key.Ed25519Public),
            EdSk = Convert.ToBase64String(key.Ed25519Seed),
            X = Convert.ToBase64String(key.X25519Public),
            XSk = Convert.ToBase64String(key.X25519Private)
        };
        return JsonSerializer.Serialize(dto);
    }

    public static IdentityPrivateKey DeserializePrivate(string json)
    {
        Require.NotNull(json);
        var dto = JsonSerializer.Deserialize<PrivateDto>(json)
                  ?? throw new FormatException("Invalid identity private key JSON.");
        if (dto.V != FormatVersion)
            throw new FormatException($"Unsupported identity private key version {dto.V}.");
        return new IdentityPrivateKey(
            Convert.FromBase64String(dto.EdSk),
            Convert.FromBase64String(dto.Ed),
            Convert.FromBase64String(dto.XSk),
            Convert.FromBase64String(dto.X));
    }

    public static bool TryDeserializePrivate(string? json, out IdentityPrivateKey key)
    {
        key = null!;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            key = DeserializePrivate(json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when JSON is legacy RSA (<c>M</c>/<c>E</c>) rather than identity v2.</summary>
    public static bool IsLegacyRsaJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        if (TryDeserializePublic(json, out _) || TryDeserializePrivate(json, out _))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.TryGetProperty("M", out _) && root.TryGetProperty("E", out _) &&
                   !root.TryGetProperty("v", out _) && !root.TryGetProperty("V", out _);
        }
        catch
        {
            return false;
        }
    }

    public static bool PublicKeyJsonEquals(string? a, string? b)
    {
        if (a is null || b is null)
            return string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b);
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b))
            return true;
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal))
            return true;
        if (!TryDeserializePublic(a, out var ka) || !TryDeserializePublic(b, out var kb))
            return false;
        return ka.Ed25519Public.AsSpan().SequenceEqual(kb.Ed25519Public) &&
               ka.X25519Public.AsSpan().SequenceEqual(kb.X25519Public);
    }

    private sealed class PublicDto
    {
        public int V { get; set; }
        public string Ed { get; set; } = "";
        public string X { get; set; } = "";
    }

    private sealed class PrivateDto
    {
        public int V { get; set; }
        public string Ed { get; set; } = "";
        public string EdSk { get; set; } = "";
        public string X { get; set; } = "";
        public string XSk { get; set; } = "";
    }
}
