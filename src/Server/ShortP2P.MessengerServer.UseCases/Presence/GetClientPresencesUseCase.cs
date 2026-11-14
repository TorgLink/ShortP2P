using Microsoft.Extensions.Options;
using ShortP2P.MessengerServer.Domain;
using ShortP2P.MessengerServer.UseCases.Abstractions;

namespace ShortP2P.MessengerServer.UseCases.Presence;

/// <summary>
/// Lists registered clients and their presence.
/// Online means any device touched the API within <see cref="MessengerInboxOptions.OnlineTimeout"/>.
/// </summary>
public sealed class GetClientPresencesUseCase(
    IClientAccountRepository accounts,
    IClientStatusRepository statuses,
    IClock clock,
    IOptions<MessengerInboxOptions> inboxOptions)
{
    public async Task<IReadOnlyList<ClientPresenceInfo>> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        var onlineTimeout = inboxOptions.Value.OnlineTimeout;
        var allAccounts = await accounts.ListAllAsync(cancellationToken).ConfigureAwait(false);
        var allStatuses = await statuses.ListAllAsync(cancellationToken).ConfigureAwait(false);
        var statusesByNetwork = allStatuses
            .GroupBy(s => s.NetworkId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var now = clock.UtcNow;

        return allAccounts
            .OrderBy(a => a.Nick, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.NetworkId, StringComparer.Ordinal)
            .Select(account =>
            {
                statusesByNetwork.TryGetValue(account.NetworkId, out var deviceStatuses);
                deviceStatuses ??= [];

                var lastSeen = deviceStatuses.Length > 0
                    ? deviceStatuses.Max(s => s.CreatedAtUtc)
                    : account.CreatedAtUtc;

                var online = deviceStatuses.Any(s =>
                    s.Status == ClientOnlineStatus.Online &&
                    now - s.CreatedAtUtc <= onlineTimeout);

                return new ClientPresenceInfo(
                    account.NetworkId,
                    account.Nick,
                    online ? ClientOnlineStatus.Online : ClientOnlineStatus.Offline,
                    lastSeen,
                    account.VisibleForSearch);
            })
            .ToArray();
    }
}
