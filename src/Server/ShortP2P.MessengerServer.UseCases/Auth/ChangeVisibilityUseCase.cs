using ShortP2P.MessengerServer.UseCases.Abstractions;

namespace ShortP2P.MessengerServer.UseCases.Auth;

public sealed class ChangeVisibilityUseCase(IClientAccountRepository accounts)
{
    public async Task ExecuteAsync(ChangeVisibilityCommand command, CancellationToken cancellationToken = default)
    {
        var networkId = command.NetworkId.Trim();
        if (networkId.Length == 0)
            throw UseCaseException.Validation("NetworkId is required.");

        var existing = await accounts.FindByNetworkIdAsync(networkId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            throw UseCaseException.NotFound("Account not found.");

        await accounts.UpdateVisibilityAsync(networkId, command.VisibleForSearch, cancellationToken)
            .ConfigureAwait(false);
    }
}
