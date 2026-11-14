namespace ShortP2P.MessengerServer.Domain;

/// <summary>Registered client account on the messenger server.</summary>
public sealed class ClientAccount
{
    public required string Nick { get; init; }

    public required string NetworkId { get; init; }

    public required string PasswordSalt { get; init; }

    public required string PasswordHash { get; init; }

    /// <summary>Creation time UTC.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>
    /// When false, the account is omitted from search directories; invites and messaging still work.
    /// Default is visible.
    /// </summary>
    public bool VisibleForSearch { get; init; } = true;
}
