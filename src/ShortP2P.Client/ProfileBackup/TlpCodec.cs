using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using ShortP2P.Auth.Data;
using ShortP2P.Crypto;

namespace ShortP2P.Client.ProfileBackup;

/// <summary>
/// <c>.tlp</c> container: binary header + ChaCha20-Poly1305(CBOR).
/// </summary>
public static class TlpCodec
{
    public static ReadOnlySpan<byte> Magic => "TLP1"u8;
    public const ushort FormatVersion = 1;
    public const ushort FlagServers = 1 << 0;

    public const int HeaderSize =
        4 + // magic
        2 + // format_version
        2 + // flags
        1 + // kdf_id
        4 + // kdf_iterations
        ProfileExportCrypto.KdfSaltSize +
        ProfileExportCrypto.NonceSize;

    public static byte[] Pack(ProfileBackupDocument document, string masterPassword, int kdfIterations = ProfileExportCrypto.ExportKdfIterations)
    {
        Require.NotNull(document);
        Require.NotNull(masterPassword);
        ValidateDocument(document);

        var plaintext = EncodeCbor(document);
        var kdfSalt = ProfileExportCrypto.CreateKdfSalt();
        var nonce = ProfileExportCrypto.CreateNonce();
        var key = ProfileExportCrypto.DeriveKey(masterPassword, kdfSalt, kdfIterations);
        try
        {
            var cipherAndTag = ProfileExportCrypto.Encrypt(plaintext, key, nonce);
            ushort flags = FlagServers;

            var file = new byte[HeaderSize + cipherAndTag.Length];
            var span = file.AsSpan();
            Magic.CopyTo(span);
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..], FormatVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(span[6..], flags);
            span[8] = ProfileExportCrypto.KdfIdPbkdf2Sha256;
            BinaryPrimitives.WriteUInt32LittleEndian(span[9..], (uint)kdfIterations);
            kdfSalt.CopyTo(span[13..]);
            nonce.CopyTo(span[45..]);
            cipherAndTag.CopyTo(span[HeaderSize..]);
            return file;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static ProfileBackupDocument Unpack(ReadOnlySpan<byte> file, string masterPassword)
    {
        Require.NotNull(masterPassword);
        if (file.Length < HeaderSize + ProfileExportCrypto.TagSize)
            throw new CryptographicException("File too short.");

        if (!file[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Invalid .tlp magic.");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported .tlp version {version}.");

        var kdfId = file[8];
        if (kdfId != ProfileExportCrypto.KdfIdPbkdf2Sha256)
            throw new InvalidDataException($"Unsupported KDF id {kdfId}.");

        var iterations = BinaryPrimitives.ReadUInt32LittleEndian(file[9..]);
        if (iterations == 0)
            throw new InvalidDataException("Invalid KDF iterations.");

        var kdfSalt = file.Slice(13, ProfileExportCrypto.KdfSaltSize);
        var nonce = file.Slice(45, ProfileExportCrypto.NonceSize);
        var cipherAndTag = file[HeaderSize..];

        var key = ProfileExportCrypto.DeriveKey(masterPassword, kdfSalt, (int)iterations);
        try
        {
            var plaintext = ProfileExportCrypto.Decrypt(cipherAndTag, key, nonce);
            var doc = DecodeCbor(plaintext);
            ValidateDocument(doc);
            return doc;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static ProfileBackupPreview ToPreview(ProfileBackupDocument doc) =>
        new()
        {
            Nickname = doc.User.Nickname,
            NetworkIdShort = doc.User.NetworkIdShort,
            ServerCount = doc.Servers.Count,
            ActiveServerCount = doc.Servers.Count(s => s.Active),
            Sections = doc.Manifest.Sections
        };

    public static void ValidateDocument(ProfileBackupDocument document)
    {
        Require.NotNull(document);
        Require.NotNull(document.User);
        Require.NotNull(document.User.Password);

        var salt = document.User.Password.Salt;
        if (salt.Length != PasswordHasher.SaltSize)
            throw new InvalidDataException($"Login salt must be exactly {PasswordHasher.SaltSize} bytes.");

        var hash = document.User.Password.Hash;
        if (hash.Length != PasswordHasher.KeySize)
            throw new InvalidDataException($"Login hash must be exactly {PasswordHasher.KeySize} bytes.");

        if (string.IsNullOrWhiteSpace(document.User.Nickname))
            throw new InvalidDataException("Nickname is required.");
        if (string.IsNullOrWhiteSpace(document.User.NetworkIdShort))
            throw new InvalidDataException("Network id is required.");

        if (IdentityKeySerializer.IsLegacyRsaJson(document.User.RsaPublicJson) ||
            IdentityKeySerializer.IsLegacyRsaJson(document.User.RsaPrivateJson))
            throw new InvalidDataException("Legacy RSA keys are not supported; .tlp must use v2 identity keys.");

        if (!IdentityKeySerializer.TryDeserializePublic(document.User.RsaPublicJson, out var pub) ||
            !IdentityKeySerializer.TryDeserializePrivate(document.User.RsaPrivateJson, out _))
            throw new InvalidDataException("v2 identity keys are required.");

        var expectedShort = CompressedNetworkId.FromIdentityPublicKey(pub).ToShortString();
        if (!string.Equals(document.User.NetworkIdShort.Trim(), expectedShort, StringComparison.Ordinal))
            throw new InvalidDataException("NetworkId does not match identity public key.");

        foreach (var server in document.Servers)
        {
            if (string.IsNullOrWhiteSpace(server.BaseUrl))
                throw new InvalidDataException("Server BaseUrl is required.");
            // trusted / trust_rating / active are value types — always present after decode;
            // missing CBOR keys default to false/0 which we accept as explicit values.
        }
    }

    public static byte[] EncodeCbor(ProfileBackupDocument document)
    {
        var writer = new CborWriter(CborConformanceMode.Strict);
        writer.WriteStartMap(3);

        writer.WriteTextString("manifest");
        WriteManifest(writer, document.Manifest);

        writer.WriteTextString("user");
        WriteUser(writer, document.User);

        writer.WriteTextString("servers");
        writer.WriteStartArray(document.Servers.Count);
        foreach (var s in document.Servers)
            WriteServer(writer, s);
        writer.WriteEndArray();

        writer.WriteEndMap();
        return writer.Encode();
    }

    public static ProfileBackupDocument DecodeCbor(ReadOnlySpan<byte> cbor)
    {
        var reader = new CborReader(cbor.ToArray(), CborConformanceMode.Strict);
        reader.ReadStartMap();

        ProfileBackupManifest? manifest = null;
        ProfileBackupUser? user = null;
        IReadOnlyList<ProfileBackupServer>? servers = null;

        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var key = reader.ReadTextString();
            switch (key)
            {
                case "manifest":
                    manifest = ReadManifest(reader);
                    break;
                case "user":
                    user = ReadUser(reader);
                    break;
                case "servers":
                    servers = ReadServers(reader);
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        }

        reader.ReadEndMap();

        if (manifest == null || user == null || servers == null)
            throw new InvalidDataException("CBOR payload missing required maps.");

        return new ProfileBackupDocument
        {
            Manifest = manifest,
            User = user,
            Servers = servers
        };
    }

    private static void WriteManifest(CborWriter writer, ProfileBackupManifest m)
    {
        writer.WriteStartMap(4);
        writer.WriteTextString("schema_version");
        writer.WriteInt32(m.SchemaVersion);
        writer.WriteTextString("exported_utc");
        writer.WriteInt64(m.ExportedUtcTicks);
        writer.WriteTextString("app");
        writer.WriteTextString(m.App);
        writer.WriteTextString("sections");
        writer.WriteStartArray(m.Sections.Count);
        foreach (var s in m.Sections)
            writer.WriteTextString(s);
        writer.WriteEndArray();
        writer.WriteEndMap();
    }

    private static ProfileBackupManifest ReadManifest(CborReader reader)
    {
        reader.ReadStartMap();
        var schema = 1;
        long exported = 0;
        var app = "TorgLink";
        IReadOnlyList<string> sections = ["user", "servers"];

        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var key = reader.ReadTextString();
            switch (key)
            {
                case "schema_version":
                    schema = reader.ReadInt32();
                    break;
                case "exported_utc":
                    exported = reader.ReadInt64();
                    break;
                case "app":
                    app = reader.ReadTextString();
                    break;
                case "sections":
                    sections = ReadStringArray(reader);
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        }

        reader.ReadEndMap();
        return new ProfileBackupManifest
        {
            SchemaVersion = schema,
            ExportedUtcTicks = exported,
            App = app,
            Sections = sections
        };
    }

    private static void WriteUser(CborWriter writer, ProfileBackupUser u)
    {
        var hasAvatar = u.Avatar is { Length: > 0 };
        writer.WriteStartMap(hasAvatar ? 8 : 7);
        writer.WriteTextString("nickname");
        writer.WriteTextString(u.Nickname);
        writer.WriteTextString("network_id_short");
        writer.WriteTextString(u.NetworkIdShort);
        writer.WriteTextString("password");
        WritePassword(writer, u.Password);
        writer.WriteTextString("rsa_private_json");
        writer.WriteTextString(u.RsaPrivateJson);
        writer.WriteTextString("rsa_public_json");
        writer.WriteTextString(u.RsaPublicJson);
        writer.WriteTextString("about_me");
        writer.WriteTextString(u.AboutMe ?? "");
        if (hasAvatar)
        {
            writer.WriteTextString("avatar");
            writer.WriteByteString(u.Avatar!);
        }

        writer.WriteTextString("data_udp_port");
        writer.WriteInt32(u.DataUdpPort);
        writer.WriteEndMap();
    }

    private static ProfileBackupUser ReadUser(CborReader reader)
    {
        reader.ReadStartMap();
        string nickname = "", networkId = "", rsaPriv = "", rsaPub = "", about = "";
        int udp = 17500;
        byte[]? avatar = null;
        ProfileBackupPassword? password = null;

        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var key = reader.ReadTextString();
            switch (key)
            {
                case "nickname":
                    nickname = reader.ReadTextString();
                    break;
                case "network_id_short":
                    networkId = reader.ReadTextString();
                    break;
                case "password":
                    password = ReadPassword(reader);
                    break;
                case "rsa_private_json":
                    rsaPriv = reader.ReadTextString();
                    break;
                case "rsa_public_json":
                    rsaPub = reader.ReadTextString();
                    break;
                case "about_me":
                    about = reader.ReadTextString();
                    break;
                case "avatar":
                    avatar = reader.ReadByteString();
                    break;
                case "data_udp_port":
                    udp = reader.ReadInt32();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        }

        reader.ReadEndMap();
        if (password == null)
            throw new InvalidDataException("user.password is required.");

        return new ProfileBackupUser
        {
            Nickname = nickname,
            NetworkIdShort = networkId,
            Password = password,
            RsaPrivateJson = rsaPriv,
            RsaPublicJson = rsaPub,
            AboutMe = about,
            Avatar = avatar,
            DataUdpPort = udp
        };
    }

    private static void WritePassword(CborWriter writer, ProfileBackupPassword p)
    {
        writer.WriteStartMap(4);
        writer.WriteTextString("alg");
        writer.WriteTextString(p.Alg);
        writer.WriteTextString("iterations");
        writer.WriteInt32(p.Iterations);
        writer.WriteTextString("salt");
        writer.WriteByteString(p.Salt);
        writer.WriteTextString("hash");
        writer.WriteByteString(p.Hash);
        writer.WriteEndMap();
    }

    private static ProfileBackupPassword ReadPassword(CborReader reader)
    {
        reader.ReadStartMap();
        var alg = "pbkdf2-sha256";
        var iterations = PasswordHasher.Iterations;
        byte[] salt = [], hash = [];

        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var key = reader.ReadTextString();
            switch (key)
            {
                case "alg":
                    alg = reader.ReadTextString();
                    break;
                case "iterations":
                    iterations = reader.ReadInt32();
                    break;
                case "salt":
                    salt = reader.ReadByteString();
                    break;
                case "hash":
                    hash = reader.ReadByteString();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        }

        reader.ReadEndMap();
        return new ProfileBackupPassword
        {
            Alg = alg,
            Iterations = iterations,
            Salt = salt,
            Hash = hash
        };
    }

    private static void WriteServer(CborWriter writer, ProfileBackupServer s)
    {
        writer.WriteStartMap(11);
        writer.WriteTextString("base_url");
        writer.WriteTextString(s.BaseUrl);
        writer.WriteTextString("fingerprint_sha256");
        writer.WriteTextString(s.FingerprintSha256 ?? "");
        writer.WriteTextString("trusted");
        writer.WriteBoolean(s.Trusted);
        writer.WriteTextString("trust_rating");
        writer.WriteSingle(s.TrustRating);
        writer.WriteTextString("active");
        writer.WriteBoolean(s.Active);
        writer.WriteTextString("is_registered");
        writer.WriteBoolean(s.IsRegistered);
        writer.WriteTextString("account_password");
        writer.WriteTextString(s.AccountPassword ?? "");
        writer.WriteTextString("network_id");
        writer.WriteTextString(s.NetworkId ?? "");
        writer.WriteTextString("nick");
        writer.WriteTextString(s.Nick ?? "");
        writer.WriteTextString("created_utc_ticks");
        writer.WriteInt64(s.CreatedUtcTicks);
        writer.WriteTextString("updated_utc_ticks");
        writer.WriteInt64(s.UpdatedUtcTicks);
        writer.WriteEndMap();
    }

    private static IReadOnlyList<ProfileBackupServer> ReadServers(CborReader reader)
    {
        var n = reader.ReadStartArray();
        var list = new List<ProfileBackupServer>(n ?? 0);
        while (reader.PeekState() != CborReaderState.EndArray)
            list.Add(ReadServer(reader));
        reader.ReadEndArray();
        return list;
    }

    private static ProfileBackupServer ReadServer(CborReader reader)
    {
        reader.ReadStartMap();
        var s = new ProfileBackupServerBuilder();
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var key = reader.ReadTextString();
            switch (key)
            {
                case "base_url":
                    s.BaseUrl = reader.ReadTextString();
                    break;
                case "fingerprint_sha256":
                    s.FingerprintSha256 = reader.ReadTextString();
                    break;
                case "trusted":
                    s.Trusted = reader.ReadBoolean();
                    s.HasTrusted = true;
                    break;
                case "trust_rating":
                    s.TrustRating = reader.ReadSingle();
                    s.HasTrustRating = true;
                    break;
                case "active":
                    s.Active = reader.ReadBoolean();
                    s.HasActive = true;
                    break;
                case "is_registered":
                    s.IsRegistered = reader.ReadBoolean();
                    break;
                case "account_password":
                    s.AccountPassword = reader.ReadTextString();
                    break;
                case "network_id":
                    s.NetworkId = reader.ReadTextString();
                    break;
                case "nick":
                    s.Nick = reader.ReadTextString();
                    break;
                case "created_utc_ticks":
                    s.CreatedUtcTicks = reader.ReadInt64();
                    break;
                case "updated_utc_ticks":
                    s.UpdatedUtcTicks = reader.ReadInt64();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        }

        reader.ReadEndMap();
        if (!s.HasTrusted || !s.HasTrustRating || !s.HasActive)
            throw new InvalidDataException("Server entry must include trusted, trust_rating, and active.");

        return s.Build();
    }

    private static IReadOnlyList<string> ReadStringArray(CborReader reader)
    {
        var n = reader.ReadStartArray();
        var list = new List<string>(n ?? 0);
        while (reader.PeekState() != CborReaderState.EndArray)
            list.Add(reader.ReadTextString());
        reader.ReadEndArray();
        return list;
    }

    private sealed class ProfileBackupServerBuilder
    {
        public string BaseUrl { get; set; } = "";
        public string FingerprintSha256 { get; set; } = "";
        public bool Trusted { get; set; }
        public float TrustRating { get; set; }
        public bool Active { get; set; }
        public bool IsRegistered { get; set; }
        public string AccountPassword { get; set; } = "";
        public string NetworkId { get; set; } = "";
        public string Nick { get; set; } = "";
        public long CreatedUtcTicks { get; set; }
        public long UpdatedUtcTicks { get; set; }
        public bool HasTrusted { get; set; }
        public bool HasTrustRating { get; set; }
        public bool HasActive { get; set; }

        public ProfileBackupServer Build() => new()
        {
            BaseUrl = BaseUrl,
            FingerprintSha256 = FingerprintSha256,
            Trusted = Trusted,
            TrustRating = TrustRating,
            Active = Active,
            IsRegistered = IsRegistered,
            AccountPassword = AccountPassword,
            NetworkId = NetworkId,
            Nick = Nick,
            CreatedUtcTicks = CreatedUtcTicks,
            UpdatedUtcTicks = UpdatedUtcTicks
        };
    }
}
