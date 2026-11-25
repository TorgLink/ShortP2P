using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using ShortP2P.Auth.Data;
using ShortP2P.Client.ProfileBackup;
using ShortP2P.Crypto;

namespace ShortP2P.Messenger.Tests;

public class TlpCodecTests
{
    private const int TestKdfIterations = 1_000;

    [Fact]
    public void PackUnpack_Roundtrip_PreservesUserAndServers()
    {
        var doc = SampleDocument();

        var file = TlpCodec.Pack(doc, "master-pass", TestKdfIterations);
        var restored = TlpCodec.Unpack(file, "master-pass");

        Assert.Equal(doc.Manifest.SchemaVersion, restored.Manifest.SchemaVersion);
        Assert.Equal(doc.Manifest.ExportedUtcTicks, restored.Manifest.ExportedUtcTicks);
        Assert.Equal(doc.Manifest.App, restored.Manifest.App);
        Assert.Equal(doc.Manifest.Sections, restored.Manifest.Sections);

        var u = restored.User;
        Assert.Equal("alice", u.Nickname);
        Assert.Equal(doc.User.NetworkIdShort, u.NetworkIdShort);
        Assert.Equal("pbkdf2-sha256", u.Password.Alg);
        Assert.Equal(PasswordHasher.Iterations, u.Password.Iterations);
        Assert.Equal(doc.User.Password.Salt, u.Password.Salt);
        Assert.Equal(doc.User.Password.Hash, u.Password.Hash);
        Assert.Equal(doc.User.RsaPrivateJson, u.RsaPrivateJson);
        Assert.Equal(doc.User.RsaPublicJson, u.RsaPublicJson);
        Assert.Equal("hello", u.AboutMe);
        Assert.Equal(doc.User.Avatar, u.Avatar);
        Assert.Equal(17501, u.DataUdpPort);

        Assert.Equal(2, restored.Servers.Count);
        var a = restored.Servers[0];
        Assert.Equal("https://a.example:7196", a.BaseUrl);
        Assert.Equal("fpA", a.FingerprintSha256);
        Assert.True(a.Trusted);
        Assert.Equal(0.9f, a.TrustRating);
        Assert.True(a.Active);
        Assert.True(a.IsRegistered);
        Assert.Equal("srvpw-a", a.AccountPassword);
        Assert.Equal(doc.User.NetworkIdShort, a.NetworkId);
        Assert.Equal("alice", a.Nick);
        Assert.Equal(111, a.CreatedUtcTicks);
        Assert.Equal(222, a.UpdatedUtcTicks);

        var b = restored.Servers[1];
        Assert.False(b.Trusted);
        Assert.False(b.Active);
        Assert.Equal(0.1f, b.TrustRating);
        Assert.False(b.IsRegistered);
    }

    [Fact]
    public void Pack_HeaderLayout_MatchesSpec()
    {
        var file = TlpCodec.Pack(SampleDocument(), "master-pass");

        Assert.Equal("TLP1"u8.ToArray(), file[..4]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4)));
        Assert.Equal(TlpCodec.FlagServers, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6)));
        Assert.Equal(ProfileExportCrypto.KdfIdPbkdf2Sha256, file[8]);
        Assert.Equal(ProfileExportCrypto.ExportKdfIterations,
            (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(9)));

        var kdfSalt = file.AsSpan(13, ProfileExportCrypto.KdfSaltSize);
        Assert.Equal(ProfileExportCrypto.KdfSaltSize, 32);
        Assert.False(kdfSalt.IndexOfAnyExcept((byte)0) < 0, "KDF salt must be random, not zeroed.");

        var nonce = file.AsSpan(45, ProfileExportCrypto.NonceSize);
        Assert.Equal(ProfileExportCrypto.NonceSize, 12);
        Assert.False(nonce.IndexOfAnyExcept((byte)0) < 0, "Nonce must be random, not zeroed.");

        Assert.Equal(TlpCodec.HeaderSize, 57);
        Assert.True(file.Length > TlpCodec.HeaderSize + ProfileExportCrypto.TagSize);
    }

    [Fact]
    public void Unpack_WrongMasterPassword_ThrowsCryptographic()
    {
        var file = TlpCodec.Pack(SampleDocument(), "master-pass", TestKdfIterations);
        Assert.ThrowsAny<CryptographicException>(() => TlpCodec.Unpack(file, "other-pass"));
    }

    [Fact]
    public void Unpack_TamperedCiphertext_ThrowsCryptographic()
    {
        var file = TlpCodec.Pack(SampleDocument(), "master-pass", TestKdfIterations);
        file[TlpCodec.HeaderSize + 3] ^= 0x5A;
        Assert.ThrowsAny<CryptographicException>(() => TlpCodec.Unpack(file, "master-pass"));
    }

    [Theory]
    [InlineData(10)] // inside header
    [InlineData(56)] // header minus nonce tail
    [InlineData(57)] // header only, no ciphertext/tag
    [InlineData(65)] // header + partial tag
    public void Unpack_TruncatedFile_Throws(int length)
    {
        var file = TlpCodec.Pack(SampleDocument(), "master-pass", TestKdfIterations);
        var truncated = file[..length];
        Assert.ThrowsAny<CryptographicException>(() => TlpCodec.Unpack(truncated, "master-pass"));
    }

    [Fact]
    public void Unpack_TagCutFromEnd_ThrowsCryptographic()
    {
        var file = TlpCodec.Pack(SampleDocument(), "master-pass", TestKdfIterations);
        var cut = file[..^8]; // half of the 16-byte tag missing
        Assert.ThrowsAny<CryptographicException>(() => TlpCodec.Unpack(cut, "master-pass"));
    }

    [Fact]
    public void Unpack_BadMagic_Throws()
    {
        var file = TlpCodec.Pack(SampleDocument(), "master-pass", TestKdfIterations);
        file[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => TlpCodec.Unpack(file, "master-pass"));
    }

    [Fact]
    public void Unpack_UnsupportedVersion_Throws()
    {
        var file = TlpCodec.Pack(SampleDocument(), "master-pass", TestKdfIterations);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), 2);
        Assert.Throws<InvalidDataException>(() => TlpCodec.Unpack(file, "master-pass"));
    }

    [Fact]
    public void Pack_LoginSaltNot32_Throws()
    {
        var doc = WithUserSalt(SampleDocument(), RandomBytes(16));
        Assert.Throws<InvalidDataException>(() => TlpCodec.Pack(doc, "master-pass", TestKdfIterations));
    }

    [Fact]
    public void Unpack_LoginSalt16InFile_Throws()
    {
        var cbor = EncodeCustomCbor(saltSize: 16, includeServerTrustFields: true);
        var file = PackRaw(cbor, "master-pass");
        Assert.Throws<InvalidDataException>(() => TlpCodec.Unpack(file, "master-pass"));
    }

    [Fact]
    public void Unpack_ServerWithoutTrustOrActive_Throws()
    {
        var cbor = EncodeCustomCbor(saltSize: 32, includeServerTrustFields: false);
        var file = PackRaw(cbor, "master-pass");
        var ex = Assert.Throws<InvalidDataException>(() => TlpCodec.Unpack(file, "master-pass"));
        Assert.Contains("trusted", ex.Message);
    }

    private static ProfileBackupDocument SampleDocument()
    {
        var keys = P2PCrypto.GenerateKeyPair();
        var networkId = CompressedNetworkId.FromIdentityPublicKey(keys.PublicKey).ToShortString();
        var pub = IdentityKeySerializer.SerializePublic(keys.PublicKey);
        var priv = IdentityKeySerializer.SerializePrivate(keys.PrivateKey);
        return new ProfileBackupDocument
        {
            Manifest = new ProfileBackupManifest
            {
                SchemaVersion = 2,
                ExportedUtcTicks = 638000000000000000,
                App = "TorgLink",
                Sections = ["user", "servers"]
            },
            User = new ProfileBackupUser
            {
                Nickname = "alice",
                NetworkIdShort = networkId,
                Password = new ProfileBackupPassword
                {
                    Alg = "pbkdf2-sha256",
                    Iterations = PasswordHasher.Iterations,
                    Salt = RandomBytes(PasswordHasher.SaltSize),
                    Hash = RandomBytes(PasswordHasher.KeySize)
                },
                RsaPrivateJson = priv,
                RsaPublicJson = pub,
                AboutMe = "hello",
                Avatar = [1, 2, 3],
                DataUdpPort = 17501
            },
            Servers =
            [
                new ProfileBackupServer
                {
                    BaseUrl = "https://a.example:7196",
                    FingerprintSha256 = "fpA",
                    Trusted = true,
                    TrustRating = 0.9f,
                    Active = true,
                    IsRegistered = true,
                    AccountPassword = "srvpw-a",
                    NetworkId = networkId,
                    Nick = "alice",
                    CreatedUtcTicks = 111,
                    UpdatedUtcTicks = 222
                },
                new ProfileBackupServer
                {
                    BaseUrl = "https://b.example:7196",
                    FingerprintSha256 = "fpB",
                    Trusted = false,
                    TrustRating = 0.1f,
                    Active = false,
                    IsRegistered = false,
                    AccountPassword = "srvpw-b",
                    NetworkId = networkId,
                    Nick = "alice",
                    CreatedUtcTicks = 333,
                    UpdatedUtcTicks = 444
                }
            ]
        };
    }

    /// <summary>Returns a copy of <paramref name="doc"/> with a different login salt.</summary>
    private static ProfileBackupDocument WithUserSalt(ProfileBackupDocument doc, byte[] salt) =>
        new()
        {
            Manifest = doc.Manifest,
            User = new ProfileBackupUser
            {
                Nickname = doc.User.Nickname,
                NetworkIdShort = doc.User.NetworkIdShort,
                Password = new ProfileBackupPassword
                {
                    Alg = doc.User.Password.Alg,
                    Iterations = doc.User.Password.Iterations,
                    Salt = salt,
                    Hash = doc.User.Password.Hash
                },
                RsaPrivateJson = doc.User.RsaPrivateJson,
                RsaPublicJson = doc.User.RsaPublicJson,
                AboutMe = doc.User.AboutMe,
                Avatar = doc.User.Avatar,
                DataUdpPort = doc.User.DataUdpPort
            },
            Servers = doc.Servers
        };

    /// <summary>CBOR payload with a configurable login salt size and optional server trust keys.</summary>
    private static byte[] EncodeCustomCbor(int saltSize, bool includeServerTrustFields)
    {
        var keys = P2PCrypto.GenerateKeyPair();
        var networkId = CompressedNetworkId.FromIdentityPublicKey(keys.PublicKey).ToShortString();
        var pub = IdentityKeySerializer.SerializePublic(keys.PublicKey);
        var priv = IdentityKeySerializer.SerializePrivate(keys.PrivateKey);

        var writer = new CborWriter(CborConformanceMode.Strict);
        writer.WriteStartMap(3);

        writer.WriteTextString("manifest");
        writer.WriteStartMap(4);
        writer.WriteTextString("schema_version");
        writer.WriteInt32(2);
        writer.WriteTextString("exported_utc");
        writer.WriteInt64(638000000000000000);
        writer.WriteTextString("app");
        writer.WriteTextString("TorgLink");
        writer.WriteTextString("sections");
        writer.WriteStartArray(2);
        writer.WriteTextString("user");
        writer.WriteTextString("servers");
        writer.WriteEndArray();
        writer.WriteEndMap();

        writer.WriteTextString("user");
        writer.WriteStartMap(7);
        writer.WriteTextString("nickname");
        writer.WriteTextString("alice");
        writer.WriteTextString("network_id_short");
        writer.WriteTextString(networkId);
        writer.WriteTextString("password");
        writer.WriteStartMap(4);
        writer.WriteTextString("alg");
        writer.WriteTextString("pbkdf2-sha256");
        writer.WriteTextString("iterations");
        writer.WriteInt32(PasswordHasher.Iterations);
        writer.WriteTextString("salt");
        writer.WriteByteString(RandomBytes(saltSize));
        writer.WriteTextString("hash");
        writer.WriteByteString(RandomBytes(PasswordHasher.KeySize));
        writer.WriteEndMap();
        writer.WriteTextString("rsa_private_json");
        writer.WriteTextString(priv);
        writer.WriteTextString("rsa_public_json");
        writer.WriteTextString(pub);
        writer.WriteTextString("about_me");
        writer.WriteTextString("");
        writer.WriteTextString("data_udp_port");
        writer.WriteInt32(17500);
        writer.WriteEndMap();

        writer.WriteTextString("servers");
        writer.WriteStartArray(1);
        writer.WriteStartMap(includeServerTrustFields ? 4 : 1);
        writer.WriteTextString("base_url");
        writer.WriteTextString("https://a.example:7196");
        if (includeServerTrustFields)
        {
            writer.WriteTextString("trusted");
            writer.WriteBoolean(true);
            writer.WriteTextString("trust_rating");
            writer.WriteSingle(0.8f);
            writer.WriteTextString("active");
            writer.WriteBoolean(true);
        }

        writer.WriteEndMap();
        writer.WriteEndArray();

        writer.WriteEndMap();
        return writer.Encode();
    }

    /// <summary>Wraps raw CBOR into a valid <c>.tlp</c> file (header + ChaCha20-Poly1305).</summary>
    private static byte[] PackRaw(byte[] cbor, string masterPassword)
    {
        var kdfSalt = ProfileExportCrypto.CreateKdfSalt();
        var nonce = ProfileExportCrypto.CreateNonce();
        var key = ProfileExportCrypto.DeriveKey(masterPassword, kdfSalt, TestKdfIterations);
        try
        {
            var cipherAndTag = ProfileExportCrypto.Encrypt(cbor, key, nonce);
            var file = new byte[TlpCodec.HeaderSize + cipherAndTag.Length];
            var span = file.AsSpan();
            TlpCodec.Magic.CopyTo(span);
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..], TlpCodec.FormatVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(span[6..], TlpCodec.FlagServers);
            span[8] = ProfileExportCrypto.KdfIdPbkdf2Sha256;
            BinaryPrimitives.WriteUInt32LittleEndian(span[9..], TestKdfIterations);
            kdfSalt.CopyTo(span[13..]);
            nonce.CopyTo(span[45..]);
            cipherAndTag.CopyTo(span[TlpCodec.HeaderSize..]);
            return file;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);
}
