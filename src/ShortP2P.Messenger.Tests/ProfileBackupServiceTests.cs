using System.Security.Cryptography;
using ShortP2P.Auth;
using ShortP2P.Auth.Data;
using ShortP2P.Client;
using ShortP2P.Client.Data;
using ShortP2P.Client.ProfileBackup;
using ShortP2P.Client.Services.MessengerServers;
using ShortP2P.Crypto;

namespace ShortP2P.Messenger.Tests;

public sealed class ProfileBackupServiceTests : IDisposable
{
    private const string Logopass = "Password1";
    private const string MasterPassword = "master-pass";
    private readonly string _dir;

    static ProfileBackupServiceTests()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public ProfileBackupServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tlp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort cleanup of temp sqlite files
        }
    }

    [Fact]
    public async Task Export_Import_NewDevice_RestoresUserAndServers()
    {
        var src = CreateStack("src");
        var user = await RegisterWithProfileAsync(src.Users);
        await InsertServerAsync(src.Servers, user, "https://a.example:7196", "fpA",
            trusted: true, active: true, rating: 0.9f, registered: true, accountPassword: "srvpw-a");
        await InsertServerAsync(src.Servers, user, "https://b.example:7196", "fpB",
            trusted: false, active: false, rating: 0.1f, registered: false, accountPassword: "srvpw-b");

        var (ok, err, bytes) = await src.Backup.ExportAsync(user.Id, MasterPassword);
        Assert.True(ok, err);
        Assert.NotNull(bytes);

        var dst = CreateStack("dst");
        var (result, restored) = await dst.Backup.ImportAsync(bytes!, MasterPassword);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(ProfileImportConflictKind.None, result.Conflict);
        Assert.NotNull(restored);
        Assert.Equal(user.Nickname, restored.Nickname);
        Assert.Equal(user.NetworkIdShort, restored.NetworkIdShort);
        Assert.Equal(user.PasswordSaltBase64, restored.PasswordSaltBase64);
        Assert.Equal(PasswordHasher.SaltSize, PasswordHasher.GetSaltLength(restored.PasswordSaltBase64));
        Assert.True(PasswordHasher.Verify(Logopass, restored.PasswordSaltBase64, restored.PasswordHashBase64));
        Assert.Equal(user.RsaPrivateJson, restored.RsaPrivateJson);
        Assert.Equal(user.RsaPublicJson, restored.RsaPublicJson);
        Assert.Equal("about alice", restored.AboutMe);
        Assert.Equal(new byte[] { 1, 2, 3 }, restored.Avatar);
        Assert.Equal(17600, restored.DataUdpPort);

        Assert.NotNull(result.Preview);
        Assert.Equal(user.Nickname, result.Preview.Nickname);
        Assert.Equal(2, result.Preview.ServerCount);
        Assert.Equal(1, result.Preview.ActiveServerCount);

        var servers = await dst.Servers.ListByUserAsync(restored.Id);
        Assert.Equal(2, servers.Count);

        var a = servers.Single(s => s.BaseUrl.Contains("a.example"));
        Assert.Equal("fpA", a.FingerprintSha256);
        Assert.True(a.Trusted);
        Assert.True(a.Active);
        Assert.True(a.IsRegistered);
        Assert.Equal("srvpw-a", a.AccountPassword);
        Assert.Equal(0.9f, a.TrustRating);
        Assert.Equal(user.NetworkIdShort, a.NetworkId);
        Assert.Equal(user.Nickname, a.Nick);
        Assert.Equal(111, a.CreatedUtcTicks);
        Assert.Equal(222, a.UpdatedUtcTicks);

        var b = servers.Single(s => s.BaseUrl.Contains("b.example"));
        Assert.False(b.Trusted);
        Assert.False(b.Active);
        Assert.False(b.IsRegistered);
        Assert.Equal(0.1f, b.TrustRating);
    }

    [Fact]
    public async Task Import_OverwriteSameNetworkId_ReplacesUserAndServers()
    {
        var src = CreateStack("src");
        var user = await RegisterWithProfileAsync(src.Users);
        await InsertServerAsync(src.Servers, user, "https://a.example:7196", "fpA",
            trusted: true, active: true, rating: 0.9f, registered: true, accountPassword: "srvpw-a");
        var (_, _, bytes) = await src.Backup.ExportAsync(user.Id, MasterPassword);
        Assert.NotNull(bytes);

        var dst = CreateStack("dst");
        var (first, firstUser) = await dst.Backup.ImportAsync(bytes!, MasterPassword);
        Assert.True(first.Ok, first.Error);
        Assert.NotNull(firstUser);

        // Local drift after the first restore: changed profile + an extra server.
        var local = await dst.Users.FindByIdAsync(firstUser.Id);
        Assert.NotNull(local);
        local!.AboutMe = "changed locally";
        await dst.Users.UpdateUserAsync(local);
        await InsertServerAsync(dst.Servers, firstUser, "https://zzz.example:7196", "fpZ",
            trusted: true, active: true, rating: 0.5f, registered: false, accountPassword: "srvpw-z");

        var (second, secondUser) = await dst.Backup.ImportAsync(bytes!, MasterPassword);

        Assert.True(second.Ok, second.Error);
        Assert.Equal(ProfileImportConflictKind.OverwriteSameNetworkId, second.Conflict);
        Assert.NotNull(secondUser);
        Assert.Equal(firstUser.Id, secondUser.Id);
        Assert.Equal("about alice", secondUser.AboutMe);

        var servers = await dst.Servers.ListByUserAsync(secondUser.Id);
        Assert.Single(servers);
        Assert.Equal("https://a.example:7196", servers[0].BaseUrl);
        Assert.Equal("fpA", servers[0].FingerprintSha256);
    }

    [Fact]
    public async Task Import_WrongMasterPassword_FailsWithoutWrites()
    {
        var src = CreateStack("src");
        var user = await RegisterWithProfileAsync(src.Users);
        var (_, _, bytes) = await src.Backup.ExportAsync(user.Id, MasterPassword);
        Assert.NotNull(bytes);

        var dst = CreateStack("dst");
        var (result, restored) = await dst.Backup.ImportAsync(bytes!, "wrong-master");

        Assert.False(result.Ok);
        Assert.Equal(ProfileBackupService.ErrorBadFileOrPassword, result.Error);
        Assert.Null(restored);
        Assert.Null(await dst.Users.FindByNicknameAsync(user.Nickname));
    }

    [Fact]
    public async Task Import_TruncatedFile_FailsWithoutWrites()
    {
        var src = CreateStack("src");
        var user = await RegisterWithProfileAsync(src.Users);
        var (_, _, bytes) = await src.Backup.ExportAsync(user.Id, MasterPassword);
        Assert.NotNull(bytes);

        var dst = CreateStack("dst");
        var truncated = bytes![..(bytes.Length - 8)];
        var (result, restored) = await dst.Backup.ImportAsync(truncated, MasterPassword);

        Assert.False(result.Ok);
        Assert.Equal(ProfileBackupService.ErrorBadFileOrPassword, result.Error);
        Assert.Null(restored);
        Assert.Null(await dst.Users.FindByNicknameAsync(user.Nickname));
    }

    [Fact]
    public async Task Import_NicknameTakenByOtherAccount_ReportsConflict()
    {
        var src = CreateStack("src");
        var user = await RegisterWithProfileAsync(src.Users);
        var (_, _, bytes) = await src.Backup.ExportAsync(user.Id, MasterPassword);
        Assert.NotNull(bytes);

        var dst = CreateStack("dst");
        var otherAuth = new AuthService(dst.Users, new FileSessionStorage(Path.Combine(_dir, "sess-other")));
        var (registered, registerError) = await otherAuth.RegisterAsync(user.Nickname, "OtherPass1");
        Assert.True(registered, registerError);
        Assert.NotEqual(user.NetworkIdShort, otherAuth.CurrentUser!.NetworkIdShort);

        var (result, restored) = await dst.Backup.ImportAsync(bytes!, MasterPassword);

        Assert.False(result.Ok);
        Assert.Equal(ProfileBackupService.ErrorNicknameTaken, result.Error);
        Assert.Equal(ProfileImportConflictKind.NicknameTakenByOther, result.Conflict);
        Assert.Null(restored);
    }

    [Fact]
    public async Task Export_RequiresOnlyMasterPassword()
    {
        var src = CreateStack("src");
        var user = await RegisterWithProfileAsync(src.Users);

        // No account password is involved: the master password alone protects the file.
        var (ok, err, bytes) = await src.Backup.ExportAsync(user.Id, MasterPassword);

        Assert.True(ok, err);
        Assert.NotNull(bytes);
        var doc = TlpCodec.Unpack(bytes!, MasterPassword);
        Assert.Equal(user.Nickname, doc.User.Nickname);
        Assert.Equal(PasswordHasher.SaltSize, doc.User.Password.Salt.Length);
        Assert.Equal(user.PasswordSaltBase64, Convert.ToBase64String(doc.User.Password.Salt));
    }

    [Fact]
    public async Task Export_Non32ByteSalt_FailsWithoutWrites()
    {
        var src = CreateStack("src");
        var legacy = await InsertLegacySaltUserAsync(src.Users, "bob", "BOB123456789");

        var (ok, err, bytes) = await src.Backup.ExportAsync(legacy.Id, MasterPassword);

        Assert.False(ok);
        Assert.Equal(ProfileBackupService.ErrorUnsupportedSaltSize, err);
        Assert.Null(bytes);

        // Safety-net only: export does not migrate; login does.
        var after = await src.Users.FindByIdAsync(legacy.Id);
        Assert.NotNull(after);
        Assert.Equal(legacy.PasswordSaltBase64, after!.PasswordSaltBase64);
        Assert.Equal(legacy.PasswordHashBase64, after.PasswordHashBase64);
    }

    [Fact]
    public async Task Login_Legacy16ByteSalt_MigratesThenExportSucceeds()
    {
        var src = CreateStack("src");
        var legacy = await InsertLegacySaltUserAsync(src.Users, "bob", "BOB123456789");
        Assert.Equal(16, PasswordHasher.GetSaltLength(legacy.PasswordSaltBase64));

        // Export is blocked until login upgrades the salt (no logopass asked at export).
        var (blocked, blockErr, _) = await src.Backup.ExportAsync(legacy.Id, MasterPassword);
        Assert.False(blocked);
        Assert.Equal(ProfileBackupService.ErrorUnsupportedSaltSize, blockErr);

        var sessionDir = Path.Combine(_dir, "sess-legacy");
        var auth = new AuthService(src.Users, new FileSessionStorage(sessionDir));
        var (ok, err) = await auth.LoginAsync(legacy.Nickname, Logopass);
        Assert.True(ok, err);
        Assert.NotNull(auth.CurrentUser);
        Assert.Equal(PasswordHasher.SaltSize, PasswordHasher.GetSaltLength(auth.CurrentUser!.PasswordSaltBase64));
        Assert.True(PasswordHasher.Verify(Logopass, auth.CurrentUser.PasswordSaltBase64,
            auth.CurrentUser.PasswordHashBase64));

        var stored = await src.Users.FindByIdAsync(legacy.Id);
        Assert.NotNull(stored);
        Assert.Equal(PasswordHasher.SaltSize, PasswordHasher.GetSaltLength(stored!.PasswordSaltBase64));
        Assert.NotEqual(legacy.PasswordSaltBase64, stored.PasswordSaltBase64);

        var (exported, exportErr, bytes) = await src.Backup.ExportAsync(legacy.Id, MasterPassword);
        Assert.True(exported, exportErr);
        Assert.NotNull(bytes);
        var doc = TlpCodec.Unpack(bytes!, MasterPassword);
        Assert.Equal(PasswordHasher.SaltSize, doc.User.Password.Salt.Length);
        Assert.True(PasswordHasher.Verify(Logopass, Convert.ToBase64String(doc.User.Password.Salt),
            Convert.ToBase64String(doc.User.Password.Hash)));
    }

    [Fact]
    public async Task AdoptRestoredUser_PersistsSession()
    {
        var src = CreateStack("src");
        var user = await RegisterWithProfileAsync(src.Users);
        var (_, _, bytes) = await src.Backup.ExportAsync(user.Id, MasterPassword);
        Assert.NotNull(bytes);

        var dst = CreateStack("dst");
        var (_, restored) = await dst.Backup.ImportAsync(bytes!, MasterPassword);
        Assert.NotNull(restored);

        var sessionDir = Path.Combine(_dir, "sess");
        var auth = new AuthService(dst.Users, new FileSessionStorage(sessionDir));
        await auth.AdoptRestoredUserAsync(restored!);
        Assert.Same(restored, auth.CurrentUser);

        // A fresh AuthService over the same storage restores the session (auto-login).
        var relaunched = new AuthService(dst.Users, new FileSessionStorage(sessionDir));
        Assert.True(await relaunched.TryRestoreSessionAsync());
        Assert.Equal(restored!.NetworkIdShort, relaunched.CurrentUser!.NetworkIdShort);
    }

    private async Task<UserEntity> RegisterWithProfileAsync(IUserAuthRepository users)
    {
        var sessionDir = Path.Combine(_dir, "sess-" + Guid.NewGuid().ToString("N"));
        var auth = new AuthService(users, new FileSessionStorage(sessionDir));
        var (ok, err) = await auth.RegisterAsync("alice", Logopass);
        Assert.True(ok, err);
        var user = auth.CurrentUser!;
        user.AboutMe = "about alice";
        user.Avatar = [1, 2, 3];
        user.DataUdpPort = 17600;
        await users.UpdateUserAsync(user);
        return user;
    }

    /// <summary>Account row with a pre-TRL-10 16-byte login salt (still verifiable via PBKDF2).</summary>
    private static async Task<UserEntity> InsertLegacySaltUserAsync(
        IUserAuthRepository users,
        string nickname,
        string _networkIdShort)
    {
        var salt16 = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Logopass, salt16, PasswordHasher.Iterations,
            HashAlgorithmName.SHA256, PasswordHasher.KeySize);
        var keys = P2PCrypto.GenerateKeyPair();
        var legacy = new UserEntity
        {
            Nickname = nickname,
            NetworkIdShort = CompressedNetworkId.FromIdentityPublicKey(keys.PublicKey).ToShortString(),
            PasswordSaltBase64 = Convert.ToBase64String(salt16),
            PasswordHashBase64 = Convert.ToBase64String(hash),
            RsaPrivateJson = IdentityKeySerializer.SerializePrivate(keys.PrivateKey),
            RsaPublicJson = IdentityKeySerializer.SerializePublic(keys.PublicKey),
            DataUdpPort = 17500,
            CreatedUtcTicks = DateTime.UtcNow.Ticks
        };
        await users.InsertUserAsync(legacy);
        return legacy;
    }

    private static async Task InsertServerAsync(
        IMessengerServerRepository servers,
        UserEntity user,
        string baseUrl,
        string fingerprint,
        bool trusted,
        bool active,
        float rating,
        bool registered,
        string accountPassword)
    {
        await servers.InsertAsync(new MessengerServerEntity
        {
            UserId = user.Id,
            BaseUrl = baseUrl,
            FingerprintSha256 = fingerprint,
            Trusted = trusted,
            Active = active,
            IsRegistered = registered,
            AccountPassword = accountPassword,
            NetworkId = user.NetworkIdShort,
            Nick = user.Nickname,
            TrustRating = rating,
            CreatedUtcTicks = 111,
            UpdatedUtcTicks = 222
        });
    }

    private TestStack CreateStack(string name)
    {
        var db = new AppDatabase(Path.Combine(_dir, name + ".db"));
        var users = new SqliteUserAuthRepository(db);
        var servers = new SqliteMessengerServerRepository(db);
        return new TestStack(db, users, servers, new ProfileBackupService(users, servers));
    }

    private sealed record TestStack(
        AppDatabase Db,
        SqliteUserAuthRepository Users,
        SqliteMessengerServerRepository Servers,
        ProfileBackupService Backup);
}
