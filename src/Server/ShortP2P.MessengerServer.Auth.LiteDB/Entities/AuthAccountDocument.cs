using ShortP2P.MessengerServer.Domain;

namespace ShortP2P.MessengerServer.Auth.LiteDB.Entities;

internal sealed class AuthAccountDocument
{
    public string Id { get; set; } = ""; // NetworkId
    public string Nick { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public bool VisibleForSearch { get; set; } = true;

    public ClientAccount ToDomain() => new()
    {
        NetworkId = Id,
        Nick = Nick,
        PasswordSalt = PasswordSalt,
        PasswordHash = PasswordHash,
        CreatedAtUtc = CreatedAtUtc,
        VisibleForSearch = VisibleForSearch
    };

    public static AuthAccountDocument FromDomain(ClientAccount account) => new()
    {
        Id = account.NetworkId,
        Nick = account.Nick,
        PasswordSalt = account.PasswordSalt,
        PasswordHash = account.PasswordHash,
        CreatedAtUtc = account.CreatedAtUtc,
        VisibleForSearch = account.VisibleForSearch
    };
}
