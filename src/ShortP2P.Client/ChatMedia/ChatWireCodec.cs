using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ShortP2P.Auth.Data;

namespace ShortP2P.Client.ChatMedia;

/// <summary>
///     Бинарный формат полезной нагрузки внутри шифрованного messenger-кадра (совместимость со старыми клиентами: без
///     префикса = UTF-8 текст).
/// </summary>
public static class ChatWireCodec
{
    private const byte KindText = 0x01;
    private const byte KindImage = 0x02;
    private const byte KindFile = 0x03;
    private const byte KindTransferOffer = 0x04;
    private const byte KindTransferControl = 0x05;
    private const byte KindUserInfo = 0x06;
    private const byte KindVocabOffer = 0x07;
    private const byte KindVocabAccept = 0x08;
    private const byte KindCompressedText = 0x09;
    private const byte KindSessionProbe = 0x0A;
    private static ReadOnlySpan<byte> Magic => "S2P1"u8;

    /// <summary>Магия S2P1 без успешного разбора вида — чтобы не показывать мусор как текст UTF-8.</summary>
    public static bool LooksLikeFramedWire(ReadOnlySpan<byte> payload)
    {
        return payload.Length >= Magic.Length && payload.StartsWith(Magic);
    }

    /// <summary>SHO-31: peek framed wire kind after S2P1 magic (false for legacy raw UTF-8).</summary>
    public static bool TryGetKind(ReadOnlySpan<byte> payload, out byte kind)
    {
        kind = 0;
        if (payload.Length < Magic.Length + 1 || !payload.StartsWith(Magic))
            return false;
        kind = payload[Magic.Length];
        return true;
    }

    /// <summary>
    ///     SHO-31: avatar/UserInfo and any blob-carrying ChatWire kinds must not go over Meshtastic/LoRa.
    ///     Text / compressed text / vocab / session probe remain allowed.
    /// </summary>
    public static bool IsForbiddenOnMeshtastic(byte kind) =>
        kind is KindImage or KindFile or KindTransferOffer or KindTransferControl or KindUserInfo;

    /// <summary>Short English label for Meshtastic skip logs (SHO-31).</summary>
    public static string DescribeForbiddenKind(byte kind) => kind switch
    {
        KindImage => "image",
        KindFile => "file",
        KindTransferOffer => "transfer offer",
        KindTransferControl => "transfer control",
        KindUserInfo => "UserInfo/avatar",
        _ => $"wire kind 0x{kind:X2}"
    };

    public static byte[] EncodeText(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        var buf = new byte[Magic.Length + 1 + 4 + utf8.Length];
        Magic.CopyTo(buf);
        buf[Magic.Length] = KindText;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(Magic.Length + 1, 4), (uint)utf8.Length);
        utf8.CopyTo(buf.AsSpan(Magic.Length + 1 + 4));
        return buf;
    }

    /// <summary>
    /// Compact session crypto probe. Body: version(1) | kind(1) | challenge(8).
    /// Sized so one messenger chunk encrypts to ≤ 80 bytes under AES-CBC+HMAC.
    /// </summary>
    public static byte[] EncodeSessionProbe(byte probeKind, ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != 8)
            throw new ArgumentException("Session probe challenge must be 8 bytes.", nameof(challenge));

        const int bodyLen = 1 + 1 + 8;
        var buf = new byte[Magic.Length + 1 + 4 + bodyLen];
        Magic.CopyTo(buf);
        buf[Magic.Length] = KindSessionProbe;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(Magic.Length + 1, 4), bodyLen);
        var body = buf.AsSpan(Magic.Length + 1 + 4);
        body[0] = 1; // version
        body[1] = probeKind;
        challenge.CopyTo(body[2..]);
        return buf;
    }

    public static byte[] EncodeVocabOffer(string offerText) =>
        EncodeUtf8Kind(KindVocabOffer, offerText);

    public static byte[] EncodeVocabAccept(string acceptText) =>
        EncodeUtf8Kind(KindVocabAccept, acceptText);

    public static byte[] EncodeCompressedText(string language, int dictVersion, ReadOnlySpan<byte> payload)
    {
        var lang = Encoding.UTF8.GetBytes(language.Trim().ToUpperInvariant());
        if (lang.Length is 0 or > 255)
            throw new ArgumentException("Language code length invalid.", nameof(language));
        if (dictVersion is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(dictVersion));

        var buf = new byte[Magic.Length + 1 + 1 + lang.Length + 2 + 4 + payload.Length];
        Magic.CopyTo(buf);
        buf[Magic.Length] = KindCompressedText;
        var o = Magic.Length + 1;
        buf[o++] = (byte)lang.Length;
        lang.CopyTo(buf.AsSpan(o));
        o += lang.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o, 2), (ushort)dictVersion);
        o += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o, 4), (uint)payload.Length);
        o += 4;
        payload.CopyTo(buf.AsSpan(o));
        return buf;
    }

    private static byte[] EncodeUtf8Kind(byte kind, string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text ?? "");
        var buf = new byte[Magic.Length + 1 + 4 + utf8.Length];
        Magic.CopyTo(buf);
        buf[Magic.Length] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(Magic.Length + 1, 4), (uint)utf8.Length);
        utf8.CopyTo(buf.AsSpan(Magic.Length + 1 + 4));
        return buf;
    }

    public static byte[] EncodeImage(string mimeType, ReadOnlySpan<byte> imageBytes)
    {
        var mime = Encoding.UTF8.GetBytes(mimeType.Trim());
        if (mime.Length > ushort.MaxValue)
            throw new global::System.ArgumentException("MIME type is too long.", nameof(mimeType));

        var buf = new byte[Magic.Length + 1 + 2 + mime.Length + 4 + imageBytes.Length];
        Magic.CopyTo(buf);
        buf[Magic.Length] = KindImage;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(Magic.Length + 1, 2), (ushort)mime.Length);
        mime.CopyTo(buf.AsSpan(Magic.Length + 1 + 2));
        var o = Magic.Length + 1 + 2 + mime.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o, 4), (uint)imageBytes.Length);
        imageBytes.CopyTo(buf.AsSpan(o + 4));
        return buf;
    }

    public static byte[] EncodeFile(string fileName, string mimeType, ReadOnlySpan<byte> fileBytes)
    {
        var mime = Encoding.UTF8.GetBytes(mimeType.Trim());
        if (mime.Length > ushort.MaxValue)
            throw new global::System.ArgumentException("MIME type is too long.", nameof(mimeType));

        var name = Encoding.UTF8.GetBytes(NormalizeWireFileName(fileName));
        if (name.Length > ushort.MaxValue)
            throw new global::System.ArgumentException("File name is too long.", nameof(fileName));

        var buf = new byte[Magic.Length + 1 + 2 + mime.Length + 2 + name.Length + 4 + fileBytes.Length];
        Magic.CopyTo(buf);
        buf[Magic.Length] = KindFile;
        var o = Magic.Length + 1;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o, 2), (ushort)mime.Length);
        o += 2;
        mime.CopyTo(buf.AsSpan(o));
        o += mime.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o, 2), (ushort)name.Length);
        o += 2;
        name.CopyTo(buf.AsSpan(o));
        o += name.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o, 4), (uint)fileBytes.Length);
        o += 4;
        fileBytes.CopyTo(buf.AsSpan(o));
        return buf;
    }

    public static byte[] EncodeTransferOffer(ChatWireTransferOffer offer)
    {
        return EncodeJsonFrame(KindTransferOffer, JsonSerializer.SerializeToUtf8Bytes(offer));
    }

    public static byte[] EncodeTransferControl(ChatWireTransferControl control)
    {
        return EncodeJsonFrame(KindTransferControl, JsonSerializer.SerializeToUtf8Bytes(control));
    }

    /// <summary>
    ///     TRL-7: push own AboutMe + Avatar to a peer (not shown in chat history).
    ///     Layout after kind: aboutLen u16 LE + AboutMe UTF-8 + avatarLen u32 LE + Avatar.
    ///     Oversized avatars are JPEG-compressed to <see cref="PeerProfileLimits.MaxAvatarBytes"/>;
    ///     never raw-truncated (that corrupts the image).
    /// </summary>
    public static byte[] EncodeUserInfo(string aboutMe, ReadOnlySpan<byte> avatar)
    {
        aboutMe ??= "";
        if (aboutMe.Length > PeerProfileLimits.MaxAboutMeChars)
            aboutMe = aboutMe[..PeerProfileLimits.MaxAboutMeChars];

        var aboutBytes = Encoding.UTF8.GetBytes(aboutMe);
        if (aboutBytes.Length > PeerProfileLimits.MaxAboutMeUtf8Bytes)
            aboutBytes = aboutBytes.AsSpan(0, PeerProfileLimits.MaxAboutMeUtf8Bytes).ToArray();

        ReadOnlySpan<byte> avatarSpan = avatar;
        byte[]? compressed = null;
        if (avatarSpan.Length > PeerProfileLimits.MaxAvatarBytes)
        {
            if (ImageAttachmentCompressor.TryCompressToMaxBytes(avatar, PeerProfileLimits.MaxAvatarBytes,
                    out compressed, out _))
                avatarSpan = compressed;
            else
                avatarSpan = ReadOnlySpan<byte>.Empty;
        }

        var buf = new byte[Magic.Length + 1 + 2 + aboutBytes.Length + 4 + avatarSpan.Length];
        Magic.CopyTo(buf);
        buf[Magic.Length] = KindUserInfo;
        var o = Magic.Length + 1;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o, 2), (ushort)aboutBytes.Length);
        o += 2;
        aboutBytes.CopyTo(buf.AsSpan(o));
        o += aboutBytes.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o, 4), (uint)avatarSpan.Length);
        o += 4;
        avatarSpan.CopyTo(buf.AsSpan(o));
        return buf;
    }

    private static byte[] EncodeJsonFrame(byte kind, byte[] jsonUtf8)
    {
        var buf = new byte[Magic.Length + 1 + 4 + jsonUtf8.Length];
        Magic.CopyTo(buf);
        buf[Magic.Length] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(Magic.Length + 1, 4), (uint)jsonUtf8.Length);
        jsonUtf8.CopyTo(buf.AsSpan(Magic.Length + 1 + 4));
        return buf;
    }

    private static string NormalizeWireFileName(string fileName)
    {
        var n = Path.GetFileName(fileName.Trim());
        return string.IsNullOrEmpty(n) ? "file" : n;
    }

    /// <summary>Возвращает false для «наследуемых» сообщений (сырой UTF-8 без магии).</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out ChatWireMessage? message)
    {
        message = null;
        if (payload.Length < Magic.Length + 1)
            return false;
        if (!payload.StartsWith(Magic))
            return false;

        var kind = payload[Magic.Length];
        var rest = payload.Slice(Magic.Length + 1);
        if (kind == KindText)
        {
            if (rest.Length < 4)
                return false;
            var len = BinaryPrimitives.ReadUInt32LittleEndian(rest);
            rest = rest.Slice(4);
            if (rest.Length < len)
                return false;
            var text = Utf8(rest.Slice(0, (int)len));
            message = new ChatWireText(text);
            return true;
        }

        if (kind == KindImage)
        {
            if (rest.Length < 2)
                return false;
            var mimeLen = BinaryPrimitives.ReadUInt16LittleEndian(rest);
            rest = rest.Slice(2);
            if (rest.Length < mimeLen + 4)
                return false;
            var mime = Utf8(rest.Slice(0, mimeLen));
            rest = rest.Slice(mimeLen);
            var imgLen = BinaryPrimitives.ReadUInt32LittleEndian(rest);
            rest = rest.Slice(4);
            if (rest.Length < imgLen)
                return false;
            message = new ChatWireImage(mime, rest.Slice(0, (int)imgLen).ToArray());
            return true;
        }

        if (kind == KindFile)
        {
            if (rest.Length < 2)
                return false;
            var mimeLen = BinaryPrimitives.ReadUInt16LittleEndian(rest);
            rest = rest.Slice(2);
            if (rest.Length < mimeLen + 2)
                return false;
            var mime = Utf8(rest.Slice(0, mimeLen));
            rest = rest.Slice(mimeLen);
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(rest);
            rest = rest.Slice(2);
            if (rest.Length < nameLen + 4)
                return false;
            var wireName = Utf8(rest.Slice(0, nameLen));
            rest = rest.Slice(nameLen);
            var fileLen = BinaryPrimitives.ReadUInt32LittleEndian(rest);
            rest = rest.Slice(4);
            if (rest.Length < fileLen)
                return false;
            message = new ChatWireFile(wireName, mime, rest.Slice(0, (int)fileLen).ToArray());
            return true;
        }

        if (kind is KindTransferOffer or KindTransferControl)
        {
            if (rest.Length < 4)
                return false;
            var len = BinaryPrimitives.ReadUInt32LittleEndian(rest);
            rest = rest.Slice(4);
            if (rest.Length < len)
                return false;
            var jsonBytes = rest.Slice(0, (int)len).ToArray();
            try
            {
                if (kind == KindTransferOffer)
                {
                    var offer = JsonSerializer.Deserialize<ChatWireTransferOffer>(jsonBytes);
                    if (offer == null)
                        return false;
                    message = offer;
                    return true;
                }

                var control = JsonSerializer.Deserialize<ChatWireTransferControl>(jsonBytes);
                if (control == null)
                    return false;
                message = control;
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (kind == KindUserInfo)
        {
            if (rest.Length < 2 + 4)
                return false;
            var aboutLen = BinaryPrimitives.ReadUInt16LittleEndian(rest);
            rest = rest.Slice(2);
            if (aboutLen > PeerProfileLimits.MaxAboutMeUtf8Bytes)
                return false;
            if (rest.Length < aboutLen + 4)
                return false;
            string about;
            try
            {
                about = Utf8(rest.Slice(0, aboutLen));
            }
            catch
            {
                return false;
            }

            if (about.Length > PeerProfileLimits.MaxAboutMeChars)
                about = about[..PeerProfileLimits.MaxAboutMeChars];
            rest = rest.Slice(aboutLen);
            var avatarLen = BinaryPrimitives.ReadUInt32LittleEndian(rest);
            rest = rest.Slice(4);
            // Accept legacy ≤20KB frames; store layer shrinks to MaxAvatarBytes.
            if (avatarLen > PeerProfileLimits.MaxAvatarDisplayBytes)
                return false;
            if (rest.Length < avatarLen)
                return false;
            byte[]? avatar = avatarLen == 0 ? null : rest.Slice(0, (int)avatarLen).ToArray();
            message = new ChatWireUserInfo(about, avatar);
            return true;
        }

        if (kind is KindVocabOffer or KindVocabAccept)
        {
            if (rest.Length < 4)
                return false;
            var len = BinaryPrimitives.ReadUInt32LittleEndian(rest);
            rest = rest.Slice(4);
            if (rest.Length < len)
                return false;
            var s = Utf8(rest.Slice(0, (int)len));
            message = kind == KindVocabOffer ? new ChatWireVocabOffer(s) : new ChatWireVocabAccept(s);
            return true;
        }

        if (kind == KindCompressedText)
        {
            if (rest.Length < 1)
                return false;
            var langLen = rest[0];
            rest = rest.Slice(1);
            if (rest.Length < langLen + 2 + 4)
                return false;
            var lang = Utf8(rest.Slice(0, langLen));
            rest = rest.Slice(langLen);
            var ver = BinaryPrimitives.ReadUInt16LittleEndian(rest);
            rest = rest.Slice(2);
            var payLen = BinaryPrimitives.ReadUInt32LittleEndian(rest);
            rest = rest.Slice(4);
            if (rest.Length < payLen)
                return false;
            message = new ChatWireCompressedText(lang, ver, rest.Slice(0, (int)payLen).ToArray());
            return true;
        }

        if (kind == KindSessionProbe)
        {
            if (rest.Length < 4)
                return false;
            var len = BinaryPrimitives.ReadUInt32LittleEndian(rest);
            rest = rest.Slice(4);
            if (len != 1 + 1 + 8 || rest.Length < len)
                return false;
            if (rest[0] != 1)
                return false;
            var probeKind = rest[1];
            var challenge = rest.Slice(2, 8).ToArray();
            message = new ChatWireSessionProbe(probeKind, challenge);
            return true;
        }

        return false;
    }

    private static string Utf8(ReadOnlySpan<byte> span)
    {
#if NETCOREAPP
        return Encoding.UTF8.GetString(span);
#else
        return Encoding.UTF8.GetString(span.ToArray());
#endif
    }
}
