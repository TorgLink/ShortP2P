namespace ShortP2P.Auth;

/// <summary>
///     Raised once after an in-place upgrade from legacy RSA identity to v2 Ed25519/X25519.
///     Callers must wipe chats for the user and show a client warning.
/// </summary>
public sealed class IdentityMigrationInfo
{
    public required string OldNetworkIdShort { get; init; }
    public required string NewNetworkIdShort { get; init; }
}
