using ShortP2P.MessengerServer.Domain;
using ShortP2P.MessengerServer.UseCases.Abstractions;

namespace ShortP2P.MessengerServer.UseCases.Messages;

public sealed record SubmitDeliveryReceiptCommand(
    string CallerNetworkId,
    string DeviceId,
    string MessageId,
    DateTime ReceivedAtUtc,
    bool NotifySender = true);

public sealed class SubmitDeliveryReceiptUseCase(
    IMessageRepository messages,
    IMessageCache messageCache,
    IMessageInboxRepository messageInbox,
    IMessageInboxCache messageInboxCache,
    IDeliveryTicketRepository tickets,
    IDeliveryTicketCache ticketCache,
    MessengerCacheOptions options)
{
    public async Task ExecuteAsync(
        SubmitDeliveryReceiptCommand command,
        CancellationToken cancellationToken = default)
    {
        StorageAccess.EnsureAnyStoreEnabled(options);

        var caller = command.CallerNetworkId.Trim();
        var deviceId = command.DeviceId.Trim();
        var messageId = command.MessageId.Trim();

        Domain.Message? message = null;
        if (options.CacheEnabled)
        {
            message = await StorageAccess
                .TryGetAsync(() => messageCache.FindByIdAsync(messageId, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }

        if (message is null && options.RepositoryEnabled)
        {
            message = await StorageAccess
                .TryGetAsync(() => messages.FindByIdAsync(messageId, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }

        if (message is null)
            throw UseCaseException.NotFound("Message not found.");

        if (!string.Equals(message.TgtNetworkId, caller, StringComparison.Ordinal))
            throw UseCaseException.Unauthorized("Only the message recipient can submit a delivery receipt.");

        if (command.NotifySender)
        {
            var ticket = new DeliveryTicket
            {
                MessageId = messageId,
                ReceivedAtUtc = DateTime.SpecifyKind(command.ReceivedAtUtc, DateTimeKind.Utc)
            };

            var entry = new CachedDeliveryTicket(ticket, message.SrcNetworkId);
            var writes = new List<Task<bool>>(2);
            if (options.CacheEnabled)
            {
                writes.Add(StorageAccess.TryWriteToCacheAsync(
                    cacheEnabled: true,
                    () => ticketCache.IsWriteAvailable,
                    () => ticketCache.AddAsync(entry, cancellationToken),
                    cancellationToken));
            }

            if (options.RepositoryEnabled)
                writes.Add(StorageAccess.TryWriteAsync(() => tickets.AddAsync(ticket, cancellationToken), cancellationToken));

            var results = await Task.WhenAll(writes).ConfigureAwait(false);
            if (!results.Any(ok => ok))
                throw UseCaseException.Unavailable("Failed to store delivery receipt: cache and repository are unavailable.");
        }

        // Delete this device's inbox copy; GC message when no copies remain.
        if (options.CacheEnabled)
        {
            await StorageAccess
                .TryExecuteAsync(() => messageInboxCache.RemoveAsync(messageId, deviceId, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.RepositoryEnabled)
        {
            await StorageAccess
                .TryExecuteAsync(() => messageInbox.RemoveAsync(messageId, deviceId, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }

        var remaining = 0;
        if (options.CacheEnabled)
        {
            try
            {
                remaining = Math.Max(remaining, await messageInboxCache.CountForMessageAsync(messageId, cancellationToken).ConfigureAwait(false));
            }
            catch
            {
                // ignore
            }
        }

        if (options.RepositoryEnabled)
        {
            try
            {
                remaining = Math.Max(remaining, await messageInbox.CountForMessageAsync(messageId, cancellationToken).ConfigureAwait(false));
            }
            catch
            {
                // ignore
            }
        }

        if (remaining == 0)
        {
            if (options.CacheEnabled)
            {
                await StorageAccess
                    .TryExecuteAsync(() => messageCache.RemoveByIdsAsync([messageId], cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (options.RepositoryEnabled)
            {
                await StorageAccess
                    .TryExecuteAsync(() => messages.RemoveByIdsAsync([messageId], cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
