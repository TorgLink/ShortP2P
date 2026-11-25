using System.Buffers.Binary;
using System.Security.Cryptography;
using ShortP2P.Crypto;

namespace ShortP2P.Auth.Data;

/// <summary>
///     Уникальный номер абонента в сети: 12 байт (wire), для UI — base64url-строка (~16 символов).
/// </summary>
public readonly struct CompressedNetworkId : IEquatable<CompressedNetworkId>, IComparable<CompressedNetworkId>
{
    public const int WireLength = 12;

    private readonly ulong _part0;
    private readonly uint _part1;

    public static CompressedNetworkId Empty => default;

    /// <summary>Random id (tests only). Prefer <see cref="FromIdentityPublicKey"/>.</summary>
    public static CompressedNetworkId New()
    {
        Span<byte> buf = stackalloc byte[WireLength];
#if NETCOREAPP
        RandomNumberGenerator.Fill(buf);
#else
        var tmp = new byte[WireLength];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(tmp);
        tmp.CopyTo(buf);
#endif
        return FromWireBytes(buf);
    }

    /// <summary>V2 NetworkId: first 12 bytes of SHA-256(Ed25519 public key).</summary>
    public static CompressedNetworkId FromIdentityPublicKey(IdentityPublicKey publicKey) =>
        FromWireBytes(IdentityKeyDerivation.NetworkIdWireFromPublicKey(publicKey));

    public bool MatchesIdentityPublicKey(IdentityPublicKey publicKey)
    {
        Span<byte> wire = stackalloc byte[WireLength];
        return TryWriteBytes(wire) &&
               IdentityKeyDerivation.NetworkIdMatchesPublicKey(wire, publicKey);
    }

    public static CompressedNetworkId FromWireBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != WireLength)
            throw new global::System.ArgumentException($"Wire form must be exactly {WireLength} bytes.", nameof(bytes));
        return new CompressedNetworkId(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]));
    }

    public byte[] ToWireBytes()
    {
        var buf = new byte[WireLength];
        TryWriteBytes(buf);
        return buf;
    }

    public bool TryWriteBytes(Span<byte> destination)
    {
        if (destination.Length < WireLength)
            return false;
        BinaryPrimitives.WriteUInt64LittleEndian(destination, _part0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], _part1);
        return true;
    }

    public string ToShortString()
    {
        Span<byte> buf = stackalloc byte[WireLength];
        TryWriteBytes(buf);
        return ToBase64Url(buf);
    }

    public static CompressedNetworkId FromShortString(string text)
    {
        Require.NotNull(text);
        var bytes = FromBase64Url(text);
        return FromWireBytes(bytes);
    }

    public static bool TryParseShortString(string? text, out CompressedNetworkId networkId)
    {
        networkId = Empty;
        if (text is null || string.IsNullOrWhiteSpace(text))
            return false;
        try
        {
            networkId = FromShortString(text.Trim());
            return !networkId.IsEmpty;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public bool IsEmpty => _part0 == 0 && _part1 == 0;

    public int CompareTo(CompressedNetworkId other)
    {
        var c = _part0.CompareTo(other._part0);
        return c != 0 ? c : _part1.CompareTo(other._part1);
    }

    public static bool operator ==(CompressedNetworkId a, CompressedNetworkId b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(CompressedNetworkId a, CompressedNetworkId b)
    {
        return !a.Equals(b);
    }

    public bool Equals(CompressedNetworkId other)
    {
        return _part0 == other._part0 && _part1 == other._part1;
    }

    public override bool Equals(object? obj)
    {
        return obj is CompressedNetworkId other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(_part0, _part1);
    }

    public override string ToString()
    {
        return IsEmpty ? "" : ToShortString();
    }

    private CompressedNetworkId(ulong part0, uint part1)
    {
        _part0 = part0;
        _part1 = part1;
    }

    private static string ToBase64Url(ReadOnlySpan<byte> data)
    {
        var s = Convert.ToBase64String(data.ToArray());
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static ReadOnlySpan<byte> FromBase64Url(string text)
    {
        var t = text.Replace("-", "+").Replace("_", "/");
        switch (t.Length % 4)
        {
            case 2:
                t += "==";
                break;
            case 3:
                t += "=";
                break;
        }

        var bytes = Convert.FromBase64String(t);
        return bytes.Length != WireLength
            ? throw new FormatException($"Decoded id must be {WireLength} bytes, but here: {bytes.Length}.")
            : bytes;
    }
}