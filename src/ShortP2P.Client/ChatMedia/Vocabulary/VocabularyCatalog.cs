using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ShortP2P.Client.ChatMedia.Vocabulary;

/// <summary>
///     Process-wide vocabulary catalog: scans zip packs once at startup, BLAKE3 hashes cached for life.
/// </summary>
public sealed class VocabularyCatalog
{
    private static readonly object Gate = new();
    private static VocabularyCatalog? _instance;

    private readonly IReadOnlyList<VocabularyPack> _packs;
    private readonly ILogger _logger;

    private VocabularyCatalog(IReadOnlyList<VocabularyPack> packs, ILogger logger)
    {
        _packs = packs;
        _logger = logger;
    }

    public static VocabularyCatalog Instance =>
        _instance ?? throw new InvalidOperationException(
            "VocabularyCatalog.Initialize must be called once at application startup.");

    public static bool IsInitialized => _instance != null;

    /// <summary>Scan <paramref name="directory"/> for <c>*_vocabulary_v*.zip</c>, hash once, load tokens.</summary>
    public static VocabularyCatalog Initialize(string directory, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        lock (Gate)
        {
            if (_instance != null)
                return _instance;

            var packs = new List<VocabularyPack>();
            if (Directory.Exists(directory))
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*_vocabulary_v*.zip",
                             SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var pack = VocabularyZipLoader.Load(path);
                        packs.Add(pack);
                        logger.LogInformation(
                            "Loaded vocabulary {Lang} v{Version} ({Tokens} tokens, blake3={Hash})",
                            pack.Language, pack.Version, pack.IdToWord.Count, pack.Blake3Hex);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to load vocabulary zip {Path}", path);
                    }
                }
            }
            else
            {
                logger.LogWarning("Vocabulary directory missing: {Dir}", directory);
            }

            _instance = new VocabularyCatalog(packs, logger);
            return _instance;
        }
    }

    /// <summary>Test helper: replace catalog with an in-memory set (not for production hosts).</summary>
    public static void ResetForTests(IEnumerable<VocabularyPack>? packs = null)
    {
        lock (Gate)
        {
            _instance = packs == null
                ? null
                : new VocabularyCatalog(packs.ToList(), NullLogger.Instance);
        }
    }

    public IReadOnlyList<VocabularyPack> Packs => _packs;

    public IEnumerable<VocabularyPack> PacksForLanguage(string language)
    {
        var lang = language.Trim().ToUpperInvariant();
        return _packs.Where(p => p.Language == lang).OrderByDescending(p => p.Version);
    }

    public bool TryGet(string language, int version, string blake3Hex, out VocabularyPack? pack)
    {
        var lang = language.Trim().ToUpperInvariant();
        var hash = blake3Hex.Trim().ToLowerInvariant();
        pack = _packs.FirstOrDefault(p =>
            p.Language == lang && p.Version == version &&
            string.Equals(p.Blake3Hex, hash, StringComparison.Ordinal));
        return pack != null;
    }

    /// <summary>Build offer map: lang → list of (version, blake3).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(int Version, string Blake3Hex)>> BuildOffer()
    {
        return _packs
            .GroupBy(p => p.Language, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<(int, string)>)g
                    .OrderBy(p => p.Version)
                    .Select(p => (p.Version, p.Blake3Hex))
                    .ToList(),
                StringComparer.Ordinal);
    }
}
