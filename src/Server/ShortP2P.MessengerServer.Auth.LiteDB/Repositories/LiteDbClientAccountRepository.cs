using LiteDB;
using Microsoft.Extensions.Options;
using ShortP2P.MessengerServer.Auth.LiteDB.Entities;
using ShortP2P.MessengerServer.Auth.LiteDB.Options;
using ShortP2P.MessengerServer.Domain;
using ShortP2P.MessengerServer.UseCases.Abstractions;

namespace ShortP2P.MessengerServer.Auth.LiteDB.Repositories;

public sealed class LiteDbClientAccountRepository : IClientAccountRepository, IDisposable
{
    private readonly ILiteDatabase _db;
    private readonly ILiteCollection<AuthAccountDocument> _accounts;
    private readonly object _gate = new();

    public LiteDbClientAccountRepository(IOptions<AuthLiteDbOptions> options)
    {
        var cs = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("Auth:LiteDb:ConnectionString is required.");

        _db = new LiteDatabase(cs);
        _accounts = _db.GetCollection<AuthAccountDocument>("auth_accounts");
        _accounts.EnsureIndex(x => x.Nick, unique: true);
    }

    public Task<ClientAccount?> FindByNetworkIdAsync(
        string networkId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var doc = _accounts.FindById(networkId);
            return Task.FromResult(doc?.ToDomain());
        }
    }

    public Task<ClientAccount?> FindByNickAsync(string nick, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var doc = _accounts.FindOne(x => x.Nick == nick);
            return Task.FromResult(doc?.ToDomain());
        }
    }

    public Task AddAsync(ClientAccount account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_accounts.Exists(x => x.Id == account.NetworkId || x.Nick == account.Nick))
                throw new InvalidOperationException("Account with the same networkId or nick already exists.");

            _accounts.Insert(AuthAccountDocument.FromDomain(account));
        }

        return Task.CompletedTask;
    }

    public Task UpdateVisibilityAsync(
        string networkId,
        bool visibleForSearch,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var doc = _accounts.FindById(networkId)
                ?? throw new InvalidOperationException($"Account '{networkId}' was not found.");
            doc.VisibleForSearch = visibleForSearch;
            _accounts.Update(doc);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ClientAccount>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<ClientAccount> list = _accounts.FindAll()
                .Select(x => x.ToDomain())
                .ToArray();
            return Task.FromResult(list);
        }
    }

    public void Dispose() => _db.Dispose();
}
