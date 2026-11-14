using ShortP2P.MessengerServer.Domain;

namespace ShortP2P.MessengerServer.Auth.EntityFramework.Entities;

public sealed class AuthAccountEntity
{
    public string NetworkId { get; set; } = "";
    public string Nick { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public bool VisibleForSearch { get; set; } = true;

    public ClientAccount ToDomain() => new()
    {
        NetworkId = NetworkId,
        Nick = Nick,
        PasswordSalt = PasswordSalt,
        PasswordHash = PasswordHash,
        CreatedAtUtc = CreatedAtUtc,
        VisibleForSearch = VisibleForSearch
    };

    public static AuthAccountEntity FromDomain(ClientAccount account) => new()
    {
        NetworkId = account.NetworkId,
        Nick = account.Nick,
        PasswordSalt = account.PasswordSalt,
        PasswordHash = account.PasswordHash,
        CreatedAtUtc = account.CreatedAtUtc,
        VisibleForSearch = account.VisibleForSearch
    };
}
