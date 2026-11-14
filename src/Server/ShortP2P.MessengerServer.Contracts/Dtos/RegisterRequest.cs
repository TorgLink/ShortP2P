namespace ShortP2P.MessengerServer.Contracts.Dtos;

/// <summary>New client registration payload.</summary>
public sealed class RegisterRequest
{
    public required string Nick { get; init; }

    /// <summary>Short network id (base64url, ~16 chars).</summary>
    public required string NetworkId { get; init; }

    public required string Password { get; init; }

    /// <summary>Device id: lowercase hex SHA-256 (64 chars) of install GUID.</summary>
    public required string DeviceId { get; init; }

    /// <summary>
    /// When false, the new account is hidden from search. Omitted or null → visible (default).
    /// </summary>
    public bool? VisibleForSearch { get; init; }
}
