using System.Globalization;
using System.Text;

namespace ShortP2P.Client.ChatMedia.Vocabulary;

/// <summary>Parse/format vocab offer &amp; accept payloads (SMT-3).</summary>
public static class VocabularyNegotiation
{
    public const string Uncompressed = "UNCOMPRESSED";

    /// <summary>
    ///     Offer: <c>RU:1,hash;2,hash|EN:1,hash</c>
    /// </summary>
    public static string FormatOffer(IReadOnlyDictionary<string, IReadOnlyList<(int Version, string Blake3Hex)>> offer)
    {
        var sb = new StringBuilder();
        var firstLang = true;
        foreach (var (lang, versions) in offer.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (versions.Count == 0)
                continue;
            if (!firstLang)
                sb.Append('|');
            firstLang = false;
            sb.Append(lang).Append(':');
            for (var i = 0; i < versions.Count; i++)
            {
                if (i > 0)
                    sb.Append(';');
                sb.Append(versions[i].Version.ToString(CultureInfo.InvariantCulture))
                    .Append(',')
                    .Append(versions[i].Blake3Hex.ToLowerInvariant());
            }
        }

        return sb.ToString();
    }

    public static bool TryParseOffer(
        string text,
        out Dictionary<string, List<(int Version, string Blake3Hex)>> offer)
    {
        offer = new Dictionary<string, List<(int, string)>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var langPart in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = langPart.IndexOf(':');
            if (colon <= 0)
                return false;
            var lang = langPart[..colon].Trim().ToUpperInvariant();
            var rest = langPart[(colon + 1)..];
            var list = new List<(int, string)>();
            foreach (var entry in rest.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var comma = entry.IndexOf(',');
                if (comma <= 0)
                    return false;
                if (!int.TryParse(entry.AsSpan(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var ver))
                    return false;
                var hash = entry[(comma + 1)..].Trim().ToLowerInvariant();
                if (hash.Length == 0)
                    return false;
                list.Add((ver, hash));
            }

            if (list.Count == 0)
                return false;
            offer[lang] = list;
        }

        return offer.Count > 0;
    }

    /// <summary>Accept: <c>RU:2,hash|EN:UNCOMPRESSED</c></summary>
    public static string FormatAccept(IReadOnlyDictionary<string, VocabAcceptEntry> accept)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var (lang, entry) in accept.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!first)
                sb.Append('|');
            first = false;
            sb.Append(lang).Append(':');
            if (entry.IsUncompressed)
                sb.Append(Uncompressed);
            else
                sb.Append(entry.Version.ToString(CultureInfo.InvariantCulture))
                    .Append(',')
                    .Append(entry.Blake3Hex!.ToLowerInvariant());
        }

        return sb.ToString();
    }

    public static bool TryParseAccept(string text, out Dictionary<string, VocabAcceptEntry> accept)
    {
        accept = new Dictionary<string, VocabAcceptEntry>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var langPart in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = langPart.IndexOf(':');
            if (colon <= 0)
                return false;
            var lang = langPart[..colon].Trim().ToUpperInvariant();
            var rest = langPart[(colon + 1)..].Trim();
            if (string.Equals(rest, Uncompressed, StringComparison.OrdinalIgnoreCase))
            {
                accept[lang] = VocabAcceptEntry.UncompressedEntry();
                continue;
            }

            var comma = rest.IndexOf(',');
            if (comma <= 0)
                return false;
            if (!int.TryParse(rest.AsSpan(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ver))
                return false;
            var hash = rest[(comma + 1)..].Trim().ToLowerInvariant();
            if (hash.Length == 0)
                return false;
            accept[lang] = VocabAcceptEntry.Shared(ver, hash);
        }

        return accept.Count > 0;
    }

    /// <summary>
    ///     For each offered language, pick max version whose (version,hash) exists locally; else UNCOMPRESSED.
    /// </summary>
    public static Dictionary<string, VocabAcceptEntry> SelectAccept(
        IReadOnlyDictionary<string, List<(int Version, string Blake3Hex)>> offer,
        VocabularyCatalog catalog)
    {
        var result = new Dictionary<string, VocabAcceptEntry>(StringComparer.Ordinal);
        foreach (var (lang, versions) in offer)
        {
            VocabAcceptEntry? best = null;
            foreach (var (ver, hash) in versions.OrderByDescending(v => v.Version))
            {
                if (catalog.TryGet(lang, ver, hash, out _))
                {
                    best = VocabAcceptEntry.Shared(ver, hash);
                    break;
                }
            }

            result[lang] = best ?? VocabAcceptEntry.UncompressedEntry();
        }

        // Also mark local-only languages? Spec: respond per offered lang. Done.
        return result;
    }
}

public readonly struct VocabAcceptEntry
{
    public bool IsUncompressed { get; }
    public int Version { get; }
    public string? Blake3Hex { get; }

    private VocabAcceptEntry(bool uncompressed, int version, string? hash)
    {
        IsUncompressed = uncompressed;
        Version = version;
        Blake3Hex = hash;
    }

    public static VocabAcceptEntry UncompressedEntry() => new(true, 0, null);

    public static VocabAcceptEntry Shared(int version, string blake3Hex) =>
        new(false, version, blake3Hex.ToLowerInvariant());
}
