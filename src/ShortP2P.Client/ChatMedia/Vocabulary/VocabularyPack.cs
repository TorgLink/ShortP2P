namespace ShortP2P.Client.ChatMedia.Vocabulary;

/// <summary>One loaded vocabulary zip: language, version, BLAKE3 hex, token tables.</summary>
public sealed class VocabularyPack
{
    public VocabularyPack(
        string language,
        int version,
        string blake3Hex,
        IReadOnlyDictionary<int, string> idToWord,
        IReadOnlyDictionary<string, int> wordToId)
    {
        Language = language;
        Version = version;
        Blake3Hex = blake3Hex;
        IdToWord = idToWord;
        WordToId = wordToId;
    }

    /// <summary>Uppercase language code, e.g. RU, EN.</summary>
    public string Language { get; }

    public int Version { get; }

    /// <summary>Lowercase hex of BLAKE3 over the zip file bytes.</summary>
    public string Blake3Hex { get; }

    public IReadOnlyDictionary<int, string> IdToWord { get; }

    public IReadOnlyDictionary<string, int> WordToId { get; }
}
