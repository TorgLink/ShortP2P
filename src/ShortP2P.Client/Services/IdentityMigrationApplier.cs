using ShortP2P.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ShortP2P.Client.Services;

/// <summary>
///     After login/restore: if Auth migrated legacy RSA → v2 identity, wipe chats and expose warning text for UI.
/// </summary>
public static class IdentityMigrationApplier
{
    public static async Task<string?> ApplyPendingAsync(
        AuthService auth,
        ChatRepository chats,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var info = auth.PendingIdentityMigrationWarning;
        if (info == null)
            return null;

        var log = logger ?? NullLogger.Instance;
        var user = auth.CurrentUser;
        if (user == null)
        {
            auth.ClearPendingIdentityMigrationWarning();
            return null;
        }

        var deleted = await chats.DeleteAllChatsForUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
        log.LogWarning(
            "SHO-33 wiped {ChatCount} chats after identity migration {OldId} -> {NewId}",
            deleted, info.OldNetworkIdShort, info.NewNetworkIdShort);

        var message =
            $"Your network identity was upgraded for security.\n\n" +
            $"Old Network ID: {info.OldNetworkIdShort}\n" +
            $"New Network ID: {info.NewNetworkIdShort}\n\n" +
            "Previous chats were removed. Share your new QR / invite with contacts. " +
            "Restore the same identity later only from a .tlp backup.";

        auth.ClearPendingIdentityMigrationWarning();
        return message;
    }
}
