using System.Security.Cryptography;
using ShortP2P.Auth;
using ShortP2P.Auth.Data;
using ShortP2P.Client.Data;
using ShortP2P.Client.Services.MessengerServers;
using ShortP2P.Crypto;

namespace ShortP2P.Client.ProfileBackup;

/// <summary>
/// TRL-10: applies <c>.tlp</c> profile export/import to the local repositories.
/// Export needs only a master password; import restores a user on a new device
/// or overwrites the same network id.
/// </summary>
public sealed class ProfileBackupService(IUserAuthRepository users, IMessengerServerRepository servers)
{
    public const string ErrorNotLoggedIn = "Not logged in.";
    public const string ErrorMasterPasswordRequired = "Master password is required.";
    public const string ErrorUnsupportedSaltSize = "Unsupported login salt size.";
    public const string ErrorBadFileOrPassword = "Invalid master password or corrupted file.";
    public const string ErrorUnsupportedPasswordParams = "Unsupported login password parameters in file.";
    public const string ErrorMissingKeys = "RSA keys are missing in file.";
    public const string ErrorInvalidServerUrl = "Server BaseUrl is invalid.";
    public const string ErrorNicknameTaken = "Nickname is used by another local account.";

    private readonly IUserAuthRepository _users = users ?? throw new global::System.ArgumentNullException(nameof(users));
    private readonly IMessengerServerRepository _servers = servers ?? throw new global::System.ArgumentNullException(nameof(servers));

    /// <summary>
    /// Packs user + messenger servers into a <c>.tlp</c> file encrypted with
    /// <paramref name="masterPassword"/> (a standalone password, not checked against anything).
    /// The account password is never asked for: the login salt is always
    /// <see cref="PasswordHasher.SaltSize"/> bytes; anything else fails with
    /// <see cref="ErrorUnsupportedSaltSize"/> and writes nothing.
    /// </summary>
    public async Task<(bool ok, string? error, byte[]? fileBytes)> ExportAsync(
        int userId,
        string masterPassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(masterPassword))
            return (false, ErrorMasterPasswordRequired, null);

        var user = await _users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user == null)
            return (false, ErrorNotLoggedIn, null);

        // The .tlp payload stores the raw 32-byte login salt; any other size means a broken row.
        if (PasswordHasher.GetSaltLength(user.PasswordSaltBase64) != PasswordHasher.SaltSize)
            return (false, ErrorUnsupportedSaltSize, null);

        var rows = await _servers.ListByUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var document = ToDocument(user, rows);
        return (true, null, TlpCodec.Pack(document, masterPassword));
    }

    /// <summary>
    /// Decrypts and validates a <c>.tlp</c> file, then applies it locally:
    /// new device restore (no user with this network id) or overwrite of the same network id
    /// (servers are replaced wholesale). Never applies anything on validation failure.
    /// </summary>
    public async Task<(ProfileImportResult result, UserEntity? user)> ImportAsync(
        byte[] fileBytes,
        string masterPassword,
        CancellationToken cancellationToken = default)
    {
        Require.NotNull(fileBytes);

        ProfileBackupDocument document;
        try
        {
            document = TlpCodec.Unpack(fileBytes, masterPassword ?? "");
        }
        catch (CryptographicException)
        {
            return (Fail(ErrorBadFileOrPassword), null);
        }
        catch (InvalidDataException ex)
        {
            return (Fail(ex.Message), null);
        }

        // Local login verify uses fixed PBKDF2 parameters; a file with different ones would
        // restore an account that cannot log in.
        if (!string.Equals(document.User.Password.Alg, "pbkdf2-sha256", StringComparison.Ordinal) ||
            document.User.Password.Iterations != PasswordHasher.Iterations)
        {
            return (Fail(ErrorUnsupportedPasswordParams), null);
        }

        if (string.IsNullOrWhiteSpace(document.User.RsaPrivateJson) ||
            string.IsNullOrWhiteSpace(document.User.RsaPublicJson))
        {
            return (Fail(ErrorMissingKeys), null);
        }

        foreach (var server in document.Servers)
        {
            try
            {
                _ = SqliteMessengerServerRepository.NormalizeBaseUrl(server.BaseUrl);
            }
            catch (global::System.ArgumentException)
            {
                return (Fail(ErrorInvalidServerUrl), null);
            }
        }

        var preview = TlpCodec.ToPreview(document);
        var networkId = document.User.NetworkIdShort.Trim();

        var byNick = await _users.FindByNicknameAsync(document.User.Nickname.Trim(), cancellationToken)
            .ConfigureAwait(false);
        if (byNick != null &&
            !string.Equals(byNick.NetworkIdShort, networkId, StringComparison.OrdinalIgnoreCase))
        {
            return (new ProfileImportResult
            {
                Ok = false,
                Error = ErrorNicknameTaken,
                Conflict = ProfileImportConflictKind.NicknameTakenByOther,
                Preview = preview
            }, null);
        }

        var existing = await _users.FindByNetworkIdShortAsync(networkId, cancellationToken)
            .ConfigureAwait(false);

        UserEntity user;
        var conflict = ProfileImportConflictKind.None;
        if (existing == null)
        {
            user = ToEntity(document.User);
            await _users.InsertUserAsync(user, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            conflict = ProfileImportConflictKind.OverwriteSameNetworkId;
            ApplyTo(existing, document.User);
            await _users.UpdateUserAsync(existing, cancellationToken).ConfigureAwait(false);
            user = existing;
        }

        await _servers.DeleteAllByUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
        foreach (var server in document.Servers)
            await _servers.InsertAsync(ToEntity(server, user.Id), cancellationToken).ConfigureAwait(false);

        return (new ProfileImportResult
        {
            Ok = true,
            Conflict = conflict,
            Preview = preview
        }, user);
    }

    private static ProfileImportResult Fail(string error) =>
        new() { Ok = false, Error = error, Conflict = ProfileImportConflictKind.None };

    private static ProfileBackupDocument ToDocument(UserEntity user, IReadOnlyList<MessengerServerEntity> rows) =>
        new()
        {
            Manifest = new ProfileBackupManifest
            {
                SchemaVersion = 2,
                ExportedUtcTicks = DateTime.UtcNow.Ticks,
                App = "TorgLink",
                Sections = ["user", "servers"]
            },
            User = new ProfileBackupUser
            {
                Nickname = user.Nickname,
                NetworkIdShort = user.NetworkIdShort,
                Password = new ProfileBackupPassword
                {
                    Alg = "pbkdf2-sha256",
                    Iterations = PasswordHasher.Iterations,
                    Salt = Convert.FromBase64String(user.PasswordSaltBase64),
                    Hash = Convert.FromBase64String(user.PasswordHashBase64)
                },
                RsaPrivateJson = user.RsaPrivateJson,
                RsaPublicJson = user.RsaPublicJson,
                AboutMe = user.AboutMe,
                Avatar = user.Avatar,
                DataUdpPort = user.DataUdpPort
            },
            Servers = rows.Select(s => new ProfileBackupServer
            {
                BaseUrl = s.BaseUrl,
                FingerprintSha256 = s.FingerprintSha256,
                Trusted = s.Trusted,
                TrustRating = s.TrustRating,
                Active = s.Active,
                IsRegistered = s.IsRegistered,
                AccountPassword = s.AccountPassword,
                NetworkId = s.NetworkId,
                Nick = s.Nick,
                CreatedUtcTicks = s.CreatedUtcTicks,
                UpdatedUtcTicks = s.UpdatedUtcTicks
            }).ToArray()
        };

    private static UserEntity ToEntity(ProfileBackupUser u) =>
        new()
        {
            Nickname = u.Nickname.Trim(),
            NetworkIdShort = u.NetworkIdShort.Trim(),
            PasswordSaltBase64 = Convert.ToBase64String(u.Password.Salt),
            PasswordHashBase64 = Convert.ToBase64String(u.Password.Hash),
            RsaPrivateJson = u.RsaPrivateJson,
            RsaPublicJson = u.RsaPublicJson,
            DataUdpPort = u.DataUdpPort,
            AboutMe = u.AboutMe ?? "",
            Avatar = u.Avatar,
            CreatedUtcTicks = DateTime.UtcNow.Ticks
        };

    /// <summary>Overwrite keeps the local row id and creation tick; everything else comes from the file.</summary>
    private static void ApplyTo(UserEntity target, ProfileBackupUser u)
    {
        target.Nickname = u.Nickname.Trim();
        target.PasswordSaltBase64 = Convert.ToBase64String(u.Password.Salt);
        target.PasswordHashBase64 = Convert.ToBase64String(u.Password.Hash);
        target.RsaPrivateJson = u.RsaPrivateJson;
        target.RsaPublicJson = u.RsaPublicJson;
        target.DataUdpPort = u.DataUdpPort;
        target.AboutMe = u.AboutMe ?? "";
        target.Avatar = u.Avatar;
    }

    private static MessengerServerEntity ToEntity(ProfileBackupServer s, int userId) =>
        new()
        {
            UserId = userId,
            BaseUrl = s.BaseUrl,
            FingerprintSha256 = s.FingerprintSha256 ?? "",
            Trusted = s.Trusted,
            TrustRating = s.TrustRating,
            Active = s.Active,
            IsRegistered = s.IsRegistered,
            AccountPassword = s.AccountPassword ?? "",
            NetworkId = s.NetworkId ?? "",
            Nick = s.Nick ?? "",
            CreatedUtcTicks = s.CreatedUtcTicks,
            UpdatedUtcTicks = s.UpdatedUtcTicks
        };
}
