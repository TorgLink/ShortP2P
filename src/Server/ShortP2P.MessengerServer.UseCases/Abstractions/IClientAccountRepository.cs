using ShortP2P.MessengerServer.Domain;

namespace ShortP2P.MessengerServer.UseCases.Abstractions;

public interface IClientAccountRepository
{
    Task<ClientAccount?> FindByNetworkIdAsync(string networkId, CancellationToken cancellationToken = default);

    Task<ClientAccount?> FindByNickAsync(string nick, CancellationToken cancellationToken = default);

    Task AddAsync(ClientAccount account, CancellationToken cancellationToken = default);

    /// <summary>Updates <see cref="ClientAccount.VisibleForSearch"/> for an existing account.</summary>
    Task UpdateVisibilityAsync(string networkId, bool visibleForSearch, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClientAccount>> ListAllAsync(CancellationToken cancellationToken = default);
}
