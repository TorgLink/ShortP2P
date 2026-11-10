using System.Text;

namespace ShortP2P.Client.ChatMedia.Vocabulary;

/// <summary>
///     Bit-pack tokens as <c>CapitalFlag (1 bit) + tokenId (varint-ish fixed width)</c>.
///     Longest dictionary substring match wins. Unmatched runs stay uncompressed: OOV escape
///     with tokenId = all-ones sentinel then UTF-8 length + bytes.
/// </summary>
public static class DictionaryTextCodec
{
    // 15-bit token ids (0..32766); 0x7FFF = OOV escape.
    private const int TokenIdBits = 15;
    private const int OovSentinel = (1 << TokenIdBits) - 1;
    private const int MaxTokenId = OovSentinel - 1;

    public static byte[]? TryEncode(string text, VocabularyPack pack)
    {
        if (string.IsNullOrEmpty(text) || pack.WordToId.Count == 0)
            return null;

        using var bits = new BitWriter();
        var i = 0;
        while (i < text.Length)
        {
            // Prefer longest dictionary match starting at i (case-insensitive via lowercase form).
            var matched = false;
            for (var len = Math.Min(64, text.Length - i); len >= 1; len--)
            {
                var slice = text.AsSpan(i, len);
                var lower = slice.ToString().ToLowerInvariant();
                if (!pack.WordToId.TryGetValue(lower, out var id) || id > MaxTokenId)
                    continue;

                var capital = char.IsUpper(slice[0]);
                bits.WriteBit(capital);
                bits.WriteBits(id, TokenIdBits);
                i += len;
                matched = true;
                break;
            }

            if (matched)
                continue;

            // OOV: unmatched run up to next dictionary match or 64 chars — sent uncompressed.
            var start = i;
            i++;
            while (i < text.Length)
            {
                var canMatch = false;
                for (var len = Math.Min(64, text.Length - i); len >= 1; len--)
                {
                    var lower = text.AsSpan(i, len).ToString().ToLowerInvariant();
                    if (pack.WordToId.ContainsKey(lower))
                    {
                        canMatch = true;
                        break;
                    }
                }

                if (canMatch)
                    break;
                i++;
                if (i - start >= 64)
                    break;
            }

            var oov = Encoding.UTF8.GetBytes(text[start..i]);
            if (oov.Length > 255)
                return null;
            bits.WriteBit(false);
            bits.WriteBits(OovSentinel, TokenIdBits);
            bits.WriteBits(oov.Length, 8);
            foreach (var b in oov)
                bits.WriteBits(b, 8);
        }

        return bits.ToArray();
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, VocabularyPack pack, out string text)
    {
        text = "";
        var bits = new BitReader(payload);
        var sb = new StringBuilder();
        try
        {
            while (bits.RemainingBits >= 1 + TokenIdBits)
            {
                var capital = bits.ReadBit();
                var id = bits.ReadBits(TokenIdBits);
                if (id == OovSentinel)
                {
                    if (bits.RemainingBits < 8)
                        return false;
                    var len = bits.ReadBits(8);
                    if (bits.RemainingBits < len * 8)
                        return false;
                    var buf = new byte[len];
                    for (var i = 0; i < len; i++)
                        buf[i] = (byte)bits.ReadBits(8);
                    sb.Append(Encoding.UTF8.GetString(buf));
                    continue;
                }

                if (!pack.IdToWord.TryGetValue(id, out var word))
                    return false;
                if (capital && word.Length > 0)
                {
                    sb.Append(char.ToUpperInvariant(word[0]));
                    if (word.Length > 1)
                        sb.Append(word.AsSpan(1));
                }
                else
                {
                    sb.Append(word);
                }
            }
        }
        catch
        {
            return false;
        }

        text = sb.ToString();
        return true;
    }

    private sealed class BitWriter : IDisposable
    {
        private readonly MemoryStream _ms = new();
        private int _acc;
        private int _n;

        public void WriteBit(bool bit) => WriteBits(bit ? 1 : 0, 1);

        public void WriteBits(int value, int width)
        {
            for (var i = width - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | ((value >> i) & 1);
                _n++;
                if (_n == 8)
                {
                    _ms.WriteByte((byte)_acc);
                    _acc = 0;
                    _n = 0;
                }
            }
        }

        public byte[] ToArray()
        {
            if (_n > 0)
            {
                _acc <<= 8 - _n;
                _ms.WriteByte((byte)_acc);
                _acc = 0;
                _n = 0;
            }

            return _ms.ToArray();
        }

        public void Dispose() => _ms.Dispose();
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _byteIndex;
        private int _bitIndex; // 0..7 from MSB

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _byteIndex = 0;
            _bitIndex = 0;
        }

        public int RemainingBits => (_data.Length - _byteIndex) * 8 - _bitIndex;

        public bool ReadBit() => ReadBits(1) != 0;

        public int ReadBits(int width)
        {
            var v = 0;
            for (var i = 0; i < width; i++)
            {
                if (_byteIndex >= _data.Length)
                    throw new InvalidOperationException("underrun");
                var bit = (_data[_byteIndex] >> (7 - _bitIndex)) & 1;
                v = (v << 1) | bit;
                _bitIndex++;
                if (_bitIndex == 8)
                {
                    _bitIndex = 0;
                    _byteIndex++;
                }
            }

            return v;
        }
    }
}
