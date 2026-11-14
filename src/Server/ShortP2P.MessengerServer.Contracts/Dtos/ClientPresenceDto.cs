namespace ShortP2P.MessengerServer.Contracts.Dtos;

/// <summary>Registered client and current online/offline presence.</summary>
public sealed class ClientPresenceDto
{
    public const string StatusOnline = "Online";
    public const string StatusOffline = "Offline";

    public required string NetworkId { get; init; }

    public required string Nick { get; init; }

    /// <summary><see cref="StatusOnline"/> or <see cref="StatusOffline"/>.</summary>
    public required string Status { get; init; }

    /// <summary>Last keep-alive (or registration) time UTC.</summary>
    public required DateTime LastSeenAtUtc { get; init; }

    /// <summary>
    /// When false, clients should hide this entry from search UI.
    /// Still returned so routing / registration checks keep working for known peers.
    /// </summary>
    public bool VisibleForSearch { get; init; } = true;

    public bool IsOnline =>
        string.Equals(Status, StatusOnline, StringComparison.OrdinalIgnoreCase);
}
