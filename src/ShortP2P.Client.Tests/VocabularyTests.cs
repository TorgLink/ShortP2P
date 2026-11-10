using System.IO.Compression;
using System.Text;
using ShortP2P.Client.ChatMedia;
using ShortP2P.Client.ChatMedia.Vocabulary;
using Xunit;

namespace ShortP2P.Client.Tests;

public class VocabularyTests
{
    [Fact]
    public void Catalog_Loads_Zip_With_Blake3_And_Tsv()
    {
        var dir = CreateTempPackDir(("RU", 1, [("0", "привет"), ("1", "мир")]));
        try
        {
            VocabularyCatalog.ResetForTests();
            var cat = VocabularyCatalog.Initialize(dir);
            Assert.Single(cat.Packs);
            var pack = cat.Packs[0];
            Assert.Equal("RU", pack.Language);
            Assert.Equal(1, pack.Version);
            Assert.False(string.IsNullOrWhiteSpace(pack.Blake3Hex));
            Assert.Equal(64, pack.Blake3Hex.Length); // BLAKE3-256 hex
            Assert.Equal(0, pack.WordToId["привет"]);
            Assert.Equal("мир", pack.IdToWord[1]);
        }
        finally
        {
            VocabularyCatalog.ResetForTests();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Negotiation_Picks_Max_Shared_Version_Hash()
    {
        var dir = CreateTempPackDir(
            ("RU", 1, [("0", "а")]),
            ("RU", 2, [("0", "б")]));
        try
        {
            VocabularyCatalog.ResetForTests();
            var cat = VocabularyCatalog.Initialize(dir);
            var v1 = cat.Packs.Single(p => p.Version == 1);
            var v2 = cat.Packs.Single(p => p.Version == 2);

            var offerText = VocabularyNegotiation.FormatOffer(new Dictionary<string, IReadOnlyList<(int, string)>>
            {
                ["RU"] = [(1, v1.Blake3Hex), (2, v2.Blake3Hex), (9, "deadbeef")]
            });
            Assert.True(VocabularyNegotiation.TryParseOffer(offerText, out var offer));
            var accept = VocabularyNegotiation.SelectAccept(offer, cat);
            Assert.False(accept["RU"].IsUncompressed);
            Assert.Equal(2, accept["RU"].Version);
            Assert.Equal(v2.Blake3Hex, accept["RU"].Blake3Hex);
        }
        finally
        {
            VocabularyCatalog.ResetForTests();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CaseFlag_RoundTrip_Privet()
    {
        var dir = CreateTempPackDir(("RU", 1, [("5", "привет")]));
        try
        {
            VocabularyCatalog.ResetForTests();
            var pack = VocabularyCatalog.Initialize(dir).Packs[0];
            var lower = DictionaryTextCodec.TryEncode("привет", pack);
            var titled = DictionaryTextCodec.TryEncode("Привет", pack);
            Assert.NotNull(lower);
            Assert.NotNull(titled);
            Assert.True(DictionaryTextCodec.TryDecode(lower, pack, out var d1));
            Assert.True(DictionaryTextCodec.TryDecode(titled, pack, out var d2));
            Assert.Equal("привет", d1);
            Assert.Equal("Привет", d2);
        }
        finally
        {
            VocabularyCatalog.ResetForTests();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Unmatched_Runs_Stay_Oov_With_Substring_Matches()
    {
        var dir = CreateTempPackDir(("EN", 1, [("0", "hell"), ("1", "world")]));
        try
        {
            VocabularyCatalog.ResetForTests();
            var pack = VocabularyCatalog.Initialize(dir).Packs[0];

            // Longest substring: "hell" compresses; trailing "o" and "!" stay OOV uncompressed.
            var mixed = DictionaryTextCodec.TryEncode("hello world!", pack);
            Assert.NotNull(mixed);
            Assert.True(DictionaryTextCodec.TryDecode(mixed, pack, out var decoded));
            Assert.Equal("hello world!", decoded);
        }
        finally
        {
            VocabularyCatalog.ResetForTests();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Mixed_Dict_And_Oov_RoundTrip()
    {
        var dir = CreateTempPackDir(("RU", 1, [("0", "привет"), ("1", "мир")]));
        try
        {
            VocabularyCatalog.ResetForTests();
            var pack = VocabularyCatalog.Initialize(dir).Packs[0];
            const string text = "Привет, xyz мир";
            var payload = DictionaryTextCodec.TryEncode(text, pack);
            Assert.NotNull(payload);
            Assert.True(DictionaryTextCodec.TryDecode(payload, pack, out var decoded));
            Assert.Equal(text, decoded);
        }
        finally
        {
            VocabularyCatalog.ResetForTests();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Wire_Vocab_And_Compressed_RoundTrip()
    {
        var offer = ChatWireCodec.EncodeVocabOffer("RU:1,abc|EN:UNCOMPRESSED".Replace("UNCOMPRESSED", "1,def"));
        Assert.True(ChatWireCodec.TryParse(offer, out var w1));
        Assert.IsType<ChatWireVocabOffer>(w1);

        var accept = ChatWireCodec.EncodeVocabAccept("RU:1,abc|EN:UNCOMPRESSED");
        Assert.True(ChatWireCodec.TryParse(accept, out var w2));
        Assert.IsType<ChatWireVocabAccept>(w2);

        var compressed = ChatWireCodec.EncodeCompressedText("RU", 1, [0x01, 0x02]);
        Assert.True(ChatWireCodec.TryParse(compressed, out var w3));
        var ct = Assert.IsType<ChatWireCompressedText>(w3);
        Assert.Equal("RU", ct.Language);
        Assert.Equal(1, ct.DictVersion);
        Assert.Equal(new byte[] { 0x01, 0x02 }, ct.Payload);
    }

    private static string CreateTempPackDir(params (string Lang, int Ver, (string Idx, string Word)[] Rows)[] packs)
    {
        var dir = Path.Combine(Path.GetTempPath(), "smt3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (lang, ver, rows) in packs)
        {
            var zipPath = Path.Combine(dir, $"{lang}_vocabulary_v{ver}.zip");
            using var fs = File.Create(zipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            var tokens = zip.CreateEntry("tokens.txt");
            using (var sw = new StreamWriter(tokens.Open(), new UTF8Encoding(false)))
            {
                foreach (var (idx, word) in rows)
                    sw.WriteLine($"{idx}\t{word}");
            }

            var readme = zip.CreateEntry("ReadMe.txt");
            using var rw = new StreamWriter(readme.Open(), new UTF8Encoding(false));
            rw.Write(
                "Source: hermitdave/FrequencyWords\nRepository: https://github.com/hermitdave/FrequencyWords\nLicense: Creative Commons Attribution-ShareAlike 4.0 International (CC BY-SA 4.0)\nThanks to: Hermit Dave\n");
        }

        return dir;
    }
}
