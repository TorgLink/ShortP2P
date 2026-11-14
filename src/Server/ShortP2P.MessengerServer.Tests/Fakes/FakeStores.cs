using System.Collections.Concurrent;
using ShortP2P.MessengerServer.Domain;
using ShortP2P.MessengerServer.UseCases.Abstractions;

namespace ShortP2P.MessengerServer.Tests.Fakes;

/// <summary>In-memory ports for unit tests (mirrors Api InMemoryMessengerStore behavior).</summary>
internal sealed class FakeStores
{
    public ConcurrentDictionary<string, ClientAccount> Accounts { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<(string NetworkId, string DeviceId), ClientStatuses> Statuses { get; } = new();
    public ConcurrentDictionary<string, Chat> Chats { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<(string Src, string Tgt), CryptoKeys> CryptoKeys { get; } = new();
    public ConcurrentDictionary<string, Message> Messages { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<(string MessageId, string DeviceId), MessageInboxEntry> MessageInboxes { get; } = new();
    public ConcurrentDictionary<string, ChatRequest> ChatRequests { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<(string RequestId, string DeviceId), ChatRequestInboxEntry> ChatRequestInboxes { get; } = new();
    public ConcurrentDictionary<string, (DeliveryTicket Ticket, string SrcNetworkId)> DeliveryTickets { get; } =
        new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, Blob> Blobs { get; } = new(StringComparer.Ordinal);

    public FakeClientAccountRepository AccountsRepo { get; }
    public FakeClientStatusRepository StatusesRepo { get; }
    public FakeChatRepository ChatsRepo { get; }
    public FakeChatRequestRepository ChatRequestsRepo { get; }
    public FakeCryptoKeysRepository CryptoKeysRepo { get; }
    public FakeMessageRepository MessagesRepo { get; }
    public FakeMessageInboxRepository MessageInboxRepo { get; }
    public FakeDeliveryTicketRepository TicketsRepo { get; }
    public FakeBlobRepository BlobsRepo { get; }
    public FakeMessageCache MessageCache { get; }
    public FakeMessageInboxCache MessageInboxCache { get; }
    public FakeDeliveryTicketCache TicketCache { get; }

    public FakeStores()
    {
        AccountsRepo = new FakeClientAccountRepository(this);
        StatusesRepo = new FakeClientStatusRepository(this);
        ChatsRepo = new FakeChatRepository(this);
        ChatRequestsRepo = new FakeChatRequestRepository(this);
        CryptoKeysRepo = new FakeCryptoKeysRepository(this);
        MessagesRepo = new FakeMessageRepository(this);
        MessageInboxRepo = new FakeMessageInboxRepository(this);
        TicketsRepo = new FakeDeliveryTicketRepository(this);
        BlobsRepo = new FakeBlobRepository(this);
        MessageCache = new FakeMessageCache();
        MessageInboxCache = new FakeMessageInboxCache();
        TicketCache = new FakeDeliveryTicketCache();
    }
}

internal sealed class FakeClientAccountRepository(FakeStores store) : IClientAccountRepository
{
    public Task<ClientAccount?> FindByNetworkIdAsync(string networkId, CancellationToken cancellationToken = default)
    {
        store.Accounts.TryGetValue(networkId, out var account);
        return Task.FromResult(account);
    }

    public Task<ClientAccount?> FindByNickAsync(string nick, CancellationToken cancellationToken = default)
    {
        var account = store.Accounts.Values.FirstOrDefault(a =>
            string.Equals(a.Nick, nick, StringComparison.Ordinal));
        return Task.FromResult(account);
    }

    public Task AddAsync(ClientAccount account, CancellationToken cancellationToken = default)
    {
        if (!store.Accounts.TryAdd(account.NetworkId, account))
            throw new InvalidOperationException("Duplicate networkId.");
        return Task.CompletedTask;
    }

    public Task UpdateVisibilityAsync(
        string networkId,
        bool visibleForSearch,
        CancellationToken cancellationToken = default)
    {
        if (!store.Accounts.TryGetValue(networkId, out var existing))
            throw new InvalidOperationException($"Account '{networkId}' was not found.");

        store.Accounts[networkId] = new ClientAccount
        {
            Nick = existing.Nick,
            NetworkId = existing.NetworkId,
            PasswordSalt = existing.PasswordSalt,
            PasswordHash = existing.PasswordHash,
            CreatedAtUtc = existing.CreatedAtUtc,
            VisibleForSearch = visibleForSearch
        };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ClientAccount>> ListAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ClientAccount>>(store.Accounts.Values.ToArray());
}

internal sealed class FakeClientStatusRepository(FakeStores store) : IClientStatusRepository
{
    public Task UpsertAsync(ClientStatuses status, CancellationToken cancellationToken = default)
    {
        store.Statuses[(status.NetworkId, status.DeviceId)] = status;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ClientStatuses>> ListAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ClientStatuses>>(store.Statuses.Values.ToArray());

    public Task<IReadOnlyList<string>> ListDeviceIdsAsync(
        string networkId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> ids = store.Statuses.Keys
            .Where(k => string.Equals(k.NetworkId, networkId, StringComparison.Ordinal))
            .Select(k => k.DeviceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(ids);
    }
}

internal sealed class FakeChatRepository(FakeStores store) : IChatRepository
{
    public Task<IReadOnlyList<Chat>> ListByNetworkIdAsync(
        string networkId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Chat> list = store.Chats.Values
            .Where(c => c.NetworkIds.Contains(networkId, StringComparer.Ordinal))
            .OrderBy(c => c.CreatedAtUtc)
            .ToArray();
        return Task.FromResult(list);
    }

    public Task<Chat?> FindByParticipantsAsync(
        string networkIdA,
        string networkIdB,
        CancellationToken cancellationToken = default)
    {
        var chat = store.Chats.Values.FirstOrDefault(c =>
            c.NetworkIds.Contains(networkIdA, StringComparer.Ordinal) &&
            c.NetworkIds.Contains(networkIdB, StringComparer.Ordinal) &&
            c.NetworkIds.Count == 2);
        return Task.FromResult(chat);
    }

    public Task AddAsync(Chat chat, CancellationToken cancellationToken = default)
    {
        store.Chats[chat.ChatId] = chat;
        return Task.CompletedTask;
    }
}

internal sealed class FakeChatRequestRepository(FakeStores store) : IChatRequestRepository
{
    public Task AddAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        store.ChatRequests[request.RequestId] = request;
        return Task.CompletedTask;
    }

    public Task AddInboxAsync(ChatRequestInboxEntry entry, CancellationToken cancellationToken = default)
    {
        store.ChatRequestInboxes.TryAdd((entry.RequestId, entry.DeviceId), entry);
        return Task.CompletedTask;
    }

    public Task<ChatRequest?> FindByIdAsync(string requestId, CancellationToken cancellationToken = default)
    {
        store.ChatRequests.TryGetValue(requestId, out var request);
        return Task.FromResult(request);
    }

    public Task<bool> InboxExistsAsync(
        string requestId,
        string deviceId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(store.ChatRequestInboxes.ContainsKey((requestId, deviceId)));

    public Task<IReadOnlyList<ChatRequest>> TakeForDeviceAsync(
        string targetNetworkId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var inboxKeys = store.ChatRequestInboxes
            .Where(kv =>
                string.Equals(kv.Value.TargetNetworkId, targetNetworkId, StringComparison.Ordinal) &&
                string.Equals(kv.Value.DeviceId, deviceId, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToArray();

        if (inboxKeys.Length == 0)
            return Task.FromResult<IReadOnlyList<ChatRequest>>([]);

        var requests = new List<ChatRequest>();
        foreach (var key in inboxKeys)
        {
            store.ChatRequestInboxes.TryRemove(key, out _);
            if (store.ChatRequests.TryGetValue(key.RequestId, out var request))
                requests.Add(request);
        }

        foreach (var requestId in inboxKeys.Select(k => k.RequestId).Distinct(StringComparer.Ordinal))
        {
            if (!store.ChatRequestInboxes.Keys.Any(k => k.RequestId == requestId))
                store.ChatRequests.TryRemove(requestId, out _);
        }

        return Task.FromResult<IReadOnlyList<ChatRequest>>(
            requests.OrderBy(r => r.CreatedAtUtc).ToArray());
    }

    public Task<IReadOnlyList<ChatRequest>> ListByTargetNetworkIdAsync(
        string targetNetworkId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatRequest> list = store.ChatRequests.Values
            .Where(r => string.Equals(r.TargetNetworkId, targetNetworkId, StringComparison.Ordinal))
            .OrderBy(r => r.CreatedAtUtc)
            .ToArray();
        return Task.FromResult(list);
    }

    public Task RemoveOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        var ids = store.ChatRequests.Values
            .Where(r => r.CreatedAtUtc < cutoffUtc)
            .Select(r => r.RequestId)
            .ToArray();

        foreach (var id in ids)
        {
            store.ChatRequests.TryRemove(id, out _);
            foreach (var key in store.ChatRequestInboxes.Keys.Where(k => k.RequestId == id).ToArray())
                store.ChatRequestInboxes.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeCryptoKeysRepository(FakeStores store) : ICryptoKeysRepository
{
    public Task UpsertAsync(CryptoKeys keys, CancellationToken cancellationToken = default)
    {
        store.CryptoKeys[(keys.SrcNetworkId, keys.TgtNetworkId)] = keys;
        return Task.CompletedTask;
    }

    public Task<CryptoKeys?> FindAsync(
        string srcNetworkId,
        string tgtNetworkId,
        CancellationToken cancellationToken = default)
    {
        store.CryptoKeys.TryGetValue((srcNetworkId.Trim(), tgtNetworkId.Trim()), out var keys);
        return Task.FromResult(keys);
    }
}

internal sealed class FakeMessageRepository(FakeStores store) : IMessageRepository
{
    public bool ThrowOnWrite { get; set; }

    public Task<Message?> FindByIdAsync(string messageId, CancellationToken cancellationToken = default)
    {
        store.Messages.TryGetValue(messageId, out var message);
        return Task.FromResult(message);
    }

    public Task AddAsync(Message message, CancellationToken cancellationToken = default)
    {
        if (ThrowOnWrite)
            throw new InvalidOperationException("Repository unavailable.");
        store.Messages.TryAdd(message.MessageId, message);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Message>> ListByTargetNetworkIdAsync(
        string tgtNetworkId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Message> list = store.Messages.Values
            .Where(m => string.Equals(m.TgtNetworkId, tgtNetworkId, StringComparison.Ordinal))
            .ToArray();
        return Task.FromResult(list);
    }

    public Task RemoveByIdsAsync(IReadOnlyCollection<string> messageIds, CancellationToken cancellationToken = default)
    {
        foreach (var id in messageIds)
            store.Messages.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task RemoveOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        foreach (var id in store.Messages.Values.Where(m => m.CreatedUtc < cutoffUtc).Select(m => m.MessageId).ToArray())
            store.Messages.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}

internal sealed class FakeMessageInboxRepository(FakeStores store) : IMessageInboxRepository
{
    public Task AddAsync(MessageInboxEntry entry, CancellationToken cancellationToken = default)
    {
        store.MessageInboxes.TryAdd((entry.MessageId, entry.DeviceId), entry);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(
        string messageId,
        string deviceId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(store.MessageInboxes.ContainsKey((messageId, deviceId)));

    public Task<IReadOnlyList<Message>> ListMessagesForDeviceAsync(
        string tgtNetworkId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var ids = store.MessageInboxes.Values
            .Where(e =>
                string.Equals(e.TgtNetworkId, tgtNetworkId, StringComparison.Ordinal) &&
                string.Equals(e.DeviceId, deviceId, StringComparison.Ordinal))
            .Select(e => e.MessageId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        IReadOnlyList<Message> list = ids
            .Select(id => store.Messages.TryGetValue(id, out var m) ? m : null)
            .Where(m => m is not null)
            .Cast<Message>()
            .ToArray();
        return Task.FromResult(list);
    }

    public Task RemoveAsync(
        string messageId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        store.MessageInboxes.TryRemove((messageId, deviceId), out _);
        return Task.CompletedTask;
    }

    public Task<int> CountForMessageAsync(string messageId, CancellationToken cancellationToken = default) =>
        Task.FromResult(store.MessageInboxes.Keys.Count(k => k.MessageId == messageId));

    public Task RemoveAllForMessageAsync(string messageId, CancellationToken cancellationToken = default)
    {
        foreach (var key in store.MessageInboxes.Keys.Where(k => k.MessageId == messageId).ToArray())
            store.MessageInboxes.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

internal sealed class FakeDeliveryTicketRepository(FakeStores store) : IDeliveryTicketRepository
{
    public bool ThrowOnWrite { get; set; }

    public Task AddAsync(DeliveryTicket ticket, CancellationToken cancellationToken = default)
    {
        if (ThrowOnWrite)
            throw new InvalidOperationException("Repository unavailable.");

        var src = store.Messages.TryGetValue(ticket.MessageId, out var message)
            ? message.SrcNetworkId
            : store.MessageCacheLookupSrc(ticket.MessageId) ?? string.Empty;

        store.DeliveryTickets.TryAdd(ticket.MessageId, (ticket, src));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeliveryTicket>> ListForSourceNetworkIdAsync(
        string srcNetworkId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DeliveryTicket> list = store.DeliveryTickets.Values
            .Where(x => string.Equals(x.SrcNetworkId, srcNetworkId, StringComparison.Ordinal))
            .Select(x => x.Ticket)
            .OrderBy(t => t.ReceivedAtUtc)
            .ToArray();
        return Task.FromResult(list);
    }

    public Task RemoveByMessageIdsAsync(
        IReadOnlyCollection<string> messageIds,
        CancellationToken cancellationToken = default)
    {
        foreach (var id in messageIds)
            store.DeliveryTickets.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}

internal sealed class FakeBlobRepository(FakeStores store) : IBlobRepository
{
    public Task<Blob?> FindByIdAsync(string blobId, CancellationToken cancellationToken = default)
    {
        store.Blobs.TryGetValue(blobId, out var blob);
        return Task.FromResult(blob);
    }

    public Task AddAsync(Blob blob, CancellationToken cancellationToken = default)
    {
        store.Blobs[blob.BlobId] = blob;
        return Task.CompletedTask;
    }

    public Task RemoveByIdAsync(string blobId, CancellationToken cancellationToken = default)
    {
        store.Blobs.TryRemove(blobId, out _);
        return Task.CompletedTask;
    }

    public Task RemoveOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        foreach (var id in store.Blobs.Values.Where(b => b.CreatedUtc < cutoffUtc).Select(b => b.BlobId).ToArray())
            store.Blobs.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}

internal static class FakeStoresExtensions
{
    public static string? MessageCacheLookupSrc(this FakeStores store, string messageId) =>
        store.MessageCache.FindByIdAsync(messageId).GetAwaiter().GetResult()?.SrcNetworkId;
}

internal sealed class FakeMessageCache : IMessageCache
{
    private readonly ConcurrentDictionary<string, Message> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _cachedAt = new(StringComparer.Ordinal);

    public bool IsWriteAvailable { get; set; } = true;
    public bool ThrowOnWrite { get; set; }

    public Task AddAsync(Message message, CancellationToken cancellationToken = default)
    {
        if (ThrowOnWrite)
            throw new InvalidOperationException("Cache unavailable.");
        if (!IsWriteAvailable)
            throw new InvalidOperationException("Cache full.");
        _entries.TryAdd(message.MessageId, message);
        _cachedAt.TryAdd(message.MessageId, DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public Task<Message?> FindByIdAsync(string messageId, CancellationToken cancellationToken = default)
    {
        _entries.TryGetValue(messageId, out var message);
        return Task.FromResult(message);
    }

    public Task<IReadOnlyList<Message>> ListByTargetNetworkIdAsync(
        string tgtNetworkId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Message> list = _entries.Values
            .Where(m => string.Equals(m.TgtNetworkId, tgtNetworkId, StringComparison.Ordinal))
            .ToArray();
        return Task.FromResult(list);
    }

    public Task RemoveByIdsAsync(IReadOnlyCollection<string> messageIds, CancellationToken cancellationToken = default)
    {
        foreach (var id in messageIds)
        {
            _entries.TryRemove(id, out _);
            _cachedAt.TryRemove(id, out _);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Message>> ListExpiredAsync(
        DateTime olderThanUtc,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Message> expired = _entries
            .Where(kv => _cachedAt.TryGetValue(kv.Key, out var at) && at < olderThanUtc)
            .Select(kv => kv.Value)
            .ToArray();
        return Task.FromResult(expired);
    }

    public void SeedCachedAt(string messageId, DateTime cachedAtUtc) =>
        _cachedAt[messageId] = cachedAtUtc;
}

internal sealed class FakeMessageInboxCache : IMessageInboxCache
{
    private readonly ConcurrentDictionary<(string MessageId, string DeviceId), MessageInboxEntry> _entries = new();

    public bool IsWriteAvailable { get; set; } = true;

    public Task AddAsync(MessageInboxEntry entry, CancellationToken cancellationToken = default)
    {
        if (!IsWriteAvailable)
            throw new InvalidOperationException("Cache full.");
        _entries.TryAdd((entry.MessageId, entry.DeviceId), entry);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(
        string messageId,
        string deviceId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.ContainsKey((messageId, deviceId)));

    public Task<IReadOnlyList<string>> ListMessageIdsForDeviceAsync(
        string tgtNetworkId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> ids = _entries.Values
            .Where(e =>
                string.Equals(e.TgtNetworkId, tgtNetworkId, StringComparison.Ordinal) &&
                string.Equals(e.DeviceId, deviceId, StringComparison.Ordinal))
            .Select(e => e.MessageId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(ids);
    }

    public Task RemoveAsync(
        string messageId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        _entries.TryRemove((messageId, deviceId), out _);
        return Task.CompletedTask;
    }

    public Task<int> CountForMessageAsync(string messageId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.Keys.Count(k => k.MessageId == messageId));

    public Task RemoveAllForMessageAsync(string messageId, CancellationToken cancellationToken = default)
    {
        foreach (var key in _entries.Keys.Where(k => k.MessageId == messageId).ToArray())
            _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

internal sealed class FakeDeliveryTicketCache : IDeliveryTicketCache
{
    private readonly ConcurrentDictionary<string, CachedDeliveryTicket> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _cachedAt = new(StringComparer.Ordinal);

    public bool IsWriteAvailable { get; set; } = true;
    public bool ThrowOnWrite { get; set; }

    public Task AddAsync(CachedDeliveryTicket entry, CancellationToken cancellationToken = default)
    {
        if (ThrowOnWrite)
            throw new InvalidOperationException("Cache unavailable.");
        if (!IsWriteAvailable)
            throw new InvalidOperationException("Cache full.");
        _entries.TryAdd(entry.Ticket.MessageId, entry);
        _cachedAt.TryAdd(entry.Ticket.MessageId, DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeliveryTicket>> ListForSourceNetworkIdAsync(
        string srcNetworkId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DeliveryTicket> list = _entries.Values
            .Where(e => string.Equals(e.SrcNetworkId, srcNetworkId, StringComparison.Ordinal))
            .Select(e => e.Ticket)
            .ToArray();
        return Task.FromResult(list);
    }

    public Task RemoveByMessageIdsAsync(
        IReadOnlyCollection<string> messageIds,
        CancellationToken cancellationToken = default)
    {
        foreach (var id in messageIds)
        {
            _entries.TryRemove(id, out _);
            _cachedAt.TryRemove(id, out _);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CachedDeliveryTicket>> ListExpiredAsync(
        DateTime olderThanUtc,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CachedDeliveryTicket> expired = _entries
            .Where(kv => _cachedAt.TryGetValue(kv.Key, out var at) && at < olderThanUtc)
            .Select(kv => kv.Value)
            .ToArray();
        return Task.FromResult(expired);
    }
}

internal sealed class FakeServerHostPowersRepository : IServerHostPowersRepository
{
    public ServerHostPowers? Current { get; set; }
    public bool ThrowOnGet { get; set; }
    public bool ThrowOnUpsert { get; set; }
    public List<ServerHostPowers> Upserts { get; } = [];

    public Task<ServerHostPowers> GetAsync(CancellationToken cancellationToken = default)
    {
        if (ThrowOnGet)
            throw new InvalidOperationException("powers store down");
        return Task.FromResult(Current ?? ServerHostPowers.CreateDefaults(DateTime.UtcNow));
    }

    public Task UpsertAsync(ServerHostPowers powers, CancellationToken cancellationToken = default)
    {
        if (ThrowOnUpsert)
            throw new InvalidOperationException("powers store down");
        Current = powers;
        Upserts.Add(powers);
        return Task.CompletedTask;
    }
}

internal sealed class FakeHostHardwareInfoProvider : IHostHardwareInfoProvider
{
    public HostHardwareInfo? Info { get; set; }

    public Task<HostHardwareInfo?> TryGetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Info);
}

internal sealed class FakeHostLoadInfoProvider : IHostLoadInfoProvider
{
    public HostLoadInfo? Info { get; set; }

    public Task<HostLoadInfo?> TryGetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Info);
}

internal sealed class FakeServerCertificateReader : IServerCertificateReader
{
    public ServerCertificateInfo Info { get; set; } =
        new("aabbccddeeff", "CN=test", DateTime.UtcNow.AddYears(1));

    public Task<ServerCertificateInfo> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Info);
}
