using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ShortP2P.Client.ChatMedia.Vocabulary;

/// <summary>
///     Host helper: locate <c>ShortP2P.Vocabulary</c> packs and initialize
///     <see cref="VocabularyCatalog" /> once (SMT-3).
/// </summary>
public static class VocabularyHost
{
    /// <summary>
    ///     Walk up from <paramref name="startDir"/> looking for a folder named ShortP2P.Vocabulary
    ///     that contains <c>*_vocabulary_v*.zip</c>.
    /// </summary>
    public static string? FindPacksDirectory(string? startDir = null)
    {
        startDir ??= AppContext.BaseDirectory;
        var dir = new DirectoryInfo(startDir);
        for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "ShortP2P.Vocabulary");
            if (Directory.Exists(candidate) &&
                Directory.EnumerateFiles(candidate, "*_vocabulary_v*.zip").Any())
                return candidate;
        }

        return null;
    }

    /// <summary>
    ///     Copy packs into <paramref name="cacheDirectory"/> and BLAKE3-hash via
    ///     <see cref="VocabularyCatalog.Initialize"/>. No-op if already initialized.
    /// </summary>
    public static VocabularyCatalog InitializeFromPacksDirectory(
        string packsDirectory,
        string cacheDirectory,
        ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        if (VocabularyCatalog.IsInitialized)
        {
            logger.LogInformation("VocabularyHost: catalog already initialized; skip copy from {Src}", packsDirectory);
            return VocabularyCatalog.Instance;
        }

        Directory.CreateDirectory(cacheDirectory);
        var copied = 0;
        foreach (var zip in Directory.EnumerateFiles(packsDirectory, "*_vocabulary_v*.zip")
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(zip);
            var dest = Path.Combine(cacheDirectory, name);
            File.Copy(zip, dest, overwrite: true);
            copied++;
            logger.LogInformation("VocabularyHost: copied {File} → {Dest}", name, dest);
        }

        logger.LogInformation(
            "VocabularyHost: copied {Count} pack(s) from {Src} → {Dst}",
            copied, packsDirectory, cacheDirectory);
        return VocabularyCatalog.Initialize(cacheDirectory, logger);
    }

    /// <summary>Find ShortP2P.Vocabulary near the app, copy into cache, initialize.</summary>
    public static bool TryInitializeNearApp(string cacheDirectory, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        if (VocabularyCatalog.IsInitialized)
        {
            logger.LogInformation("VocabularyHost: catalog already initialized");
            return true;
        }

        logger.LogInformation("VocabularyHost: searching for ShortP2P.Vocabulary from {Base}",
            AppContext.BaseDirectory);
        var packs = FindPacksDirectory();
        if (packs == null)
        {
            logger.LogWarning("VocabularyHost: ShortP2P.Vocabulary packs directory not found");
            return false;
        }

        logger.LogInformation("VocabularyHost: found packs directory {Dir}", packs);
        InitializeFromPacksDirectory(packs, cacheDirectory, logger);
        return true;
    }
}
