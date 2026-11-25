using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShortP2P.Auth.Data;
using ShortP2P.Crypto;

namespace ShortP2P.Auth;

public sealed class AuthService(IUserAuthRepository users, ISessionStorage sessionStorage, ILogger<AuthService>? logger = null)
{
    private const string SessionUserIdKey = "shortp2p_session_user_id";
    private readonly ISessionStorage _sessionStorage = sessionStorage ?? throw new ArgumentNullException(nameof(sessionStorage));
    private readonly IUserAuthRepository _users = users ?? throw new ArgumentNullException(nameof(users));
    private readonly ILogger<AuthService> _logger = logger ?? NullLogger<AuthService>.Instance;

    public UserEntity? CurrentUser { get; private set; }

    /// <summary>Set after login/restore when legacy RSA keys were replaced with v2 identity keys.</summary>
    public IdentityMigrationInfo? PendingIdentityMigrationWarning { get; private set; }

    public void ClearPendingIdentityMigrationWarning() => PendingIdentityMigrationWarning = null;

    public async Task<(bool ok, string? error)> RegisterAsync(string nickname, string password)
    {
        if (string.IsNullOrWhiteSpace(nickname) || string.IsNullOrWhiteSpace(password))
            return (false, "Nickname and password are required.");

        var existing = await _users.FindByNicknameAsync(nickname.Trim()).ConfigureAwait(false);
        if (existing != null)
            return (false, "This nickname is already registered.");

        var (salt, hash) = PasswordHasher.Hash(password);
        var keys = P2PCrypto.GenerateKeyPair();
        var networkId = CompressedNetworkId.FromIdentityPublicKey(keys.PublicKey);

        var user = new UserEntity
        {
            Nickname = nickname.Trim(),
            NetworkIdShort = networkId.ToShortString(),
            PasswordSaltBase64 = salt,
            PasswordHashBase64 = hash,
            RsaPrivateJson = IdentityKeySerializer.SerializePrivate(keys.PrivateKey),
            RsaPublicJson = IdentityKeySerializer.SerializePublic(keys.PublicKey),
            DataUdpPort = 17500,
            CreatedUtcTicks = DateTime.UtcNow.Ticks
        };

        await _users.InsertUserAsync(user).ConfigureAwait(false);
        await PersistSessionAsync(user.Id).ConfigureAwait(false);
        CurrentUser = user;
        PendingIdentityMigrationWarning = null;
        return (true, null);
    }

    public async Task<(bool ok, string? error)> LoginAsync(string nickname, string password)
    {
        if (string.IsNullOrWhiteSpace(nickname) || string.IsNullOrWhiteSpace(password))
            return (false, "Nickname and password are required.");

        var user = await _users.FindByNicknameAsync(nickname.Trim()).ConfigureAwait(false);
        if (user == null || !PasswordHasher.Verify(password, user.PasswordSaltBase64, user.PasswordHashBase64))
            return (false, "Invalid nickname or password.");

        // Transparent upgrade of legacy login salts (e.g. 16 → 32 bytes). Master-password
        // KDF salt for .tlp export is unrelated and never stored on the user row.
        if (PasswordHasher.GetSaltLength(user.PasswordSaltBase64) != PasswordHasher.SaltSize)
        {
            var (salt, hash) = PasswordHasher.Hash(password);
            user.PasswordSaltBase64 = salt;
            user.PasswordHashBase64 = hash;
            await _users.UpdateUserAsync(user).ConfigureAwait(false);
        }

        await MigrateLegacyIdentityIfNeededAsync(user).ConfigureAwait(false);
        await PersistSessionAsync(user.Id).ConfigureAwait(false);
        CurrentUser = user;
        return (true, null);
    }

    public async Task LogoutAsync()
    {
        CurrentUser = null;
        PendingIdentityMigrationWarning = null;
        _sessionStorage.Remove(SessionUserIdKey);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    ///     Подписывает локально восстановленного пользователя (TRL-10 импорт профиля из .tlp)
    ///     без проверки пароля — файл уже расшифрован мастер-паролем.
    /// </summary>
    public async Task AdoptRestoredUserAsync(UserEntity user)
    {
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        if (!IdentityKeySerializer.TryDeserializePrivate(user.RsaPrivateJson, out _) ||
            !IdentityKeySerializer.TryDeserializePublic(user.RsaPublicJson, out var pub))
            throw new InvalidOperationException("Restored profile must contain v2 identity keys.");

        var expectedId = CompressedNetworkId.FromIdentityPublicKey(pub).ToShortString();
        if (!string.Equals(user.NetworkIdShort.Trim(), expectedId, StringComparison.Ordinal))
            throw new InvalidOperationException("Restored NetworkId does not match identity public key.");

        await PersistSessionAsync(user.Id).ConfigureAwait(false);
        CurrentUser = user;
        PendingIdentityMigrationWarning = null;
    }

    public async Task<bool> TryRestoreSessionAsync()
    {
        var idStr = await _sessionStorage.GetAsync(SessionUserIdKey).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idStr) || !int.TryParse(idStr, out var id))
            return false;

        var user = await _users.FindByIdAsync(id).ConfigureAwait(false);
        if (user == null)
            return false;

        await MigrateLegacyIdentityIfNeededAsync(user).ConfigureAwait(false);
        CurrentUser = user;
        return true;
    }

    private async Task MigrateLegacyIdentityIfNeededAsync(UserEntity user)
    {
        PendingIdentityMigrationWarning = null;
        if (IdentityKeySerializer.TryDeserializePrivate(user.RsaPrivateJson, out var existingPriv) &&
            IdentityKeySerializer.TryDeserializePublic(user.RsaPublicJson, out var existingPub))
        {
            // Already v2: ensure NetworkId matches key (repair if corrupted).
            var expected = CompressedNetworkId.FromIdentityPublicKey(existingPub).ToShortString();
            if (!string.Equals(user.NetworkIdShort.Trim(), expected, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Repairing NetworkId for user {UserId}: {OldId} -> {NewId}",
                    user.Id, user.NetworkIdShort, expected);
                user.NetworkIdShort = expected;
                user.RsaPrivateJson = IdentityKeySerializer.SerializePrivate(existingPriv);
                user.RsaPublicJson = IdentityKeySerializer.SerializePublic(existingPub);
                await _users.UpdateUserAsync(user).ConfigureAwait(false);
            }

            return;
        }

        var oldId = user.NetworkIdShort;
        var keys = P2PCrypto.GenerateKeyPair();
        var newId = CompressedNetworkId.FromIdentityPublicKey(keys.PublicKey).ToShortString();
        user.NetworkIdShort = newId;
        user.RsaPrivateJson = IdentityKeySerializer.SerializePrivate(keys.PrivateKey);
        user.RsaPublicJson = IdentityKeySerializer.SerializePublic(keys.PublicKey);
        await _users.UpdateUserAsync(user).ConfigureAwait(false);

        PendingIdentityMigrationWarning = new IdentityMigrationInfo
        {
            OldNetworkIdShort = oldId,
            NewNetworkIdShort = newId
        };

        _logger.LogWarning(
            "SHO-33 identity migration for user {UserId}: NetworkId {OldId} -> {NewId}; chats must be wiped",
            user.Id, oldId, newId);
    }

    private Task PersistSessionAsync(int userId)
    {
        return _sessionStorage.SetAsync(SessionUserIdKey, userId.ToString());
    }

    public IdentityPrivateKey GetCurrentPrivateKey()
    {
        return CurrentUser == null
            ? throw new InvalidOperationException("Not logged in.")
            : IdentityKeySerializer.DeserializePrivate(CurrentUser.RsaPrivateJson);
    }

    public IdentityPublicKey GetCurrentPublicKey()
    {
        return CurrentUser == null
            ? throw new InvalidOperationException("Not logged in.")
            : IdentityKeySerializer.DeserializePublic(CurrentUser.RsaPublicJson);
    }

    /// <summary>
    ///     Обновляет локальные Avatar / AboutMe текущего пользователя (не уходят на messenger-сервер).
    /// </summary>
    public async Task<(bool ok, string? error)> UpdateProfileAsync(string? aboutMe, byte[]? avatar)
    {
        var user = CurrentUser;
        if (user == null)
            return (false, "Not logged in.");

        aboutMe ??= "";
        if (aboutMe.Length > PeerProfileLimits.MaxAboutMeChars)
            return (false, $"AboutMe max {PeerProfileLimits.MaxAboutMeChars} characters.");
        if (avatar != null && avatar.Length > PeerProfileLimits.MaxAvatarBytes)
            return (false, $"Avatar max {PeerProfileLimits.MaxAvatarBytes / 1024} KB.");

        user.AboutMe = aboutMe;
        user.Avatar = avatar is { Length: 0 } ? null : avatar;
        await _users.UpdateUserAsync(user).ConfigureAwait(false);
        CurrentUser = user;
        return (true, null);
    }
}
