using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ShortP2P.Client.ChatMedia.Vocabulary;

/// <summary>
///     Loads <c>&lt;Lang&gt;_vocabulary_v&lt;version&gt;.zip</c> with <c>tokens.txt</c>
///     lines <c>index\tword</c> (lowercase) and optional English <c>ReadMe.txt</c>.
/// </summary>
public static class VocabularyZipLoader
{
    private static readonly Regex FileNameRx = new(
        @"^(?<lang>[A-Za-z]{2,8})_vocabulary_v(?<ver>\d+)\.zip$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParseFileName(string fileName, out string language, out int version)
    {
        language = "";
        version = 0;
        var m = FileNameRx.Match(Path.GetFileName(fileName));
        if (!m.Success)
            return false;
        language = m.Groups["lang"].Value.ToUpperInvariant();
        version = int.Parse(m.Groups["ver"].Value);
        return version >= 0;
    }

    public static VocabularyPack Load(string zipPath)
    {
        if (!TryParseFileName(zipPath, out var lang, out var version))
            throw new InvalidOperationException($"Unexpected vocabulary zip name: {Path.GetFileName(zipPath)}");

        var bytes = File.ReadAllBytes(zipPath);
        var hashHex = Convert.ToHexString(Blake3.Hasher.Hash(bytes).AsSpan()).ToLowerInvariant();

        using var ms = new MemoryStream(bytes, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry("tokens.txt")
                    ?? throw new InvalidOperationException($"{Path.GetFileName(zipPath)} is missing tokens.txt");

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var idToWord = new Dictionary<int, string>();
        var wordToId = new Dictionary<string, int>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1)
                throw new InvalidOperationException(
                    $"Invalid tokens.txt line in {Path.GetFileName(zipPath)} (expected index\\tword): {line}");
            if (!int.TryParse(line.AsSpan(0, tab), out var index) || index < 0)
                throw new InvalidOperationException(
                    $"Invalid token index in {Path.GetFileName(zipPath)}: {line}");
            var word = line[(tab + 1)..].Trim().ToLowerInvariant();
            if (word.Length == 0)
                continue;
            idToWord[index] = word;
            wordToId[word] = index;
        }

        if (idToWord.Count == 0)
            throw new InvalidOperationException($"{Path.GetFileName(zipPath)} has an empty tokens.txt");

        return new VocabularyPack(lang, version, hashHex, idToWord, wordToId);
    }
}
