using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShortP2P.Auth;
using ShortP2P.Auth.Data;
using ShortP2P.Client.Data;
using ShortP2P.Client.Qr;
using ShortP2P.Client.Services;
using ShortP2P.MessengerServer.Contracts.Dtos;
using ShortP2P.MessengerServer.Http;
using ShortP2P.TrustSystem;

namespace ShortP2P.Client.Services.MessengerServers;

/// <summary>
/// Client-side registry of messenger servers: add/remove/active, fingerprint pin, register/login.
/// </summary>
public sealed class MessengerServerManager : IAsyncDisposable
{
    public static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan BlobHttpTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PingPeriod = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RatingRefreshPeriod = TimeSpan.FromMinutes(5);
    public const int MaxAskServersWorkers = 3;
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);

    private readonly AuthService _auth;
    private readonly DeviceIdProvider _deviceId;
    private readonly ConcurrentDictionary<int, MessengerServerConnection> _connections = new();
    private readonly ConcurrentDictionary<int, MessengerServerRankStats> _rankStats = new();
    private readonly ConcurrentDictionary<int, HashSet<string>> _registeredClients = new();
    private readonly ILogger<MessengerServerManager> _logger;
    private readonly IMessengerServerRepository _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _failoverBusy;

    /// <summary>
    /// Search visibility used when registering a new account on a messenger server (default true).
    /// Hosts should keep this in sync with <c>P2pRoutingSettings.VisibleForSearch</c>.
    /// </summary>
    public bool VisibleForSearch { get; set; } = true;

    public MessengerServerManager(
        IMessengerServerRepository repository,
        AuthService auth,
        DeviceIdProvider deviceId,
        ILogger<MessengerServerManager>? logger = null)
    {
        _repository = repository ?? throw new global::System.ArgumentNullException(nameof(repository));
        _auth = auth ?? throw new global::System.ArgumentNullException(nameof(auth));
        _deviceId = deviceId ?? throw new global::System.ArgumentNullException(nameof(deviceId));
        _logger = logger ?? NullLogger<MessengerServerManager>.Instance;
    }

    /// <summary>Raised when a saved server TLS fingerprint no longer matches (MITM risk).</summary>
    public event EventHandler<MessengerServerTrustThreatEventArgs>? TrustThreatDetected;

    /// <summary>Raised after a server is marked untrusted: fallback server or mesh.</summary>
    public event EventHandler<MessengerServerFailoverEventArgs>? FailoverCompleted;

    public async Task<IReadOnlyList<MessengerServerEntity>> ListAsync(CancellationToken cancellationToken = default)
    {
        var user = RequireUser();
        var all = await _repository.ListByUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return all
            .OrderByDescending(e => e.Active && e.Trusted)
            .ThenBy(e => GetRankStatsRef(e.Id),
                Comparer<MessengerServerRankStats>.Create(MessengerServerRankComparer.Compare))
            .ThenBy(e => e.Id)
            .ToList();
    }

    /// <summary>
    /// Finds a saved server with the same scheme, host and port (default ports match with or without :443/:80).
    /// </summary>
    public async Task<MessengerServerEntity?> FindExistingByEndpointAsync(
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var user = RequireUser();
        var all = await _repository.ListByUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(s => MessengerServerQrCodec.EndpointsEqual(s.BaseUrl, baseUrl));
    }

    public async Task<IReadOnlyList<MessengerServerEntity>> ListActiveTrustedAsync(
        CancellationToken cancellationToken = default)
    {
        var all = await ListAsync(cancellationToken).ConfigureAwait(false);
        return all.Where(s => s.Active && s.Trusted).ToList();
    }

    /// <summary>
    /// Marks a messenger server untrusted (same path as TLS fingerprint mismatch) and
    /// activates another available trusted server, or reports mesh fallback.
    /// </summary>
    public async Task<MessengerServerFailoverEventArgs> MarkUntrustedWithFailoverAsync(
        int? serverId,
        string? baseUrlHint,
        CancellationToken cancellationToken = default)
    {
        var user = RequireUser();
        MessengerServerEntity? target = null;
        if (serverId is > 0)
        {
            target = await _repository.GetByIdAsync(serverId.Value, cancellationToken).ConfigureAwait(false);
            if (target != null && target.UserId != user.Id)
                target = null;
        }

        if (target == null && baseUrlHint is not null && !string.IsNullOrWhiteSpace(baseUrlHint))
            target = await FindExistingByEndpointAsync(baseUrlHint, cancellationToken).ConfigureAwait(false);

        if (target == null)
        {
            var active = await ListActiveTrustedAsync(cancellationToken).ConfigureAwait(false);
            target = SortServersByRank(active).FirstOrDefault();
        }

        if (target == null)
        {
            var mesh = new MessengerServerFailoverEventArgs(null, null, switchedToMesh: true);
            FailoverCompleted?.Invoke(this, mesh);
            return mesh;
        }

        await PersistUntrustedAsync(target, cancellationToken).ConfigureAwait(false);
        await ReportClaimToPeersAsync(target, ServerClaimReason.MALFUNCTIONED, cancellationToken)
            .ConfigureAwait(false);
        return await FailoverAfterUntrustAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Snapshot of live rank metrics for UI / diagnostics.</summary>
    public MessengerServerRankStats GetRankStats(int serverId) =>
        CloneStats(_rankStats.GetOrAdd(serverId, _ => new MessengerServerRankStats()));

    public bool IsServerAvailable(int serverId) => GetRankStats(serverId).IsAvailable;

    /// <summary>Base URL of a live connection by server id, if present.</summary>
    public bool TryGetServerBaseUrl(int serverId, out string? baseUrl)
    {
        if (_connections.TryGetValue(serverId, out var conn))
        {
            baseUrl = conn.Entity.BaseUrl;
            return !string.IsNullOrWhiteSpace(baseUrl);
        }

        baseUrl = null;
        return false;
    }

    /// <summary>
    /// Статус для шапки: Disabled (серверы выкл.), Waiting, Connected, Disconnected.
    /// </summary>
    public async Task<MessengerServerLinkStatus> GetLinkStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var all = await ListAsync(cancellationToken).ConfigureAwait(false);
        if (all.Count == 0 || !all.Any(s => s.Active))
            return MessengerServerLinkStatus.Disabled;

        var candidates = all.Where(s => s.Active && s.Trusted).ToList();
        if (candidates.Count == 0)
            return MessengerServerLinkStatus.Disconnected;

        var anyConnected = false;
        var anyPending = false;
        foreach (var server in candidates)
        {
            var stats = GetRankStats(server.Id);
            if (_connections.TryGetValue(server.Id, out var conn) &&
                conn.HasValidToken &&
                stats.IsAvailable)
            {
                anyConnected = true;
                break;
            }

            if (stats.ConsecutiveFailures == 0)
                anyPending = true;
        }

        if (anyConnected)
            return MessengerServerLinkStatus.Connected;
        if (anyPending)
            return MessengerServerLinkStatus.Waiting;
        return MessengerServerLinkStatus.Disconnected;
    }

    /// <summary>Replaces the known registered-client snapshot for a trusted server (from GetClients).</summary>
    public void ReplaceRegisteredClients(int serverId, IEnumerable<string> networkIds)
    {
        Require.NotNull(networkIds);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in networkIds)
        {
            var trimmed = id.Trim();
            if (trimmed.Length > 0)
                set.Add(trimmed);
        }

        _registeredClients[serverId] = set;
    }

    public bool IsClientRegisteredOnServer(int serverId, string networkId)
    {
        if (string.IsNullOrWhiteSpace(networkId))
            return false;
        if (!_registeredClients.TryGetValue(serverId, out var set))
            return false;
        var trimmed = networkId.Trim();
        if (set.Contains(trimmed))
            return true;
        foreach (var id in set)
        {
            if (ChatRepository.PeerNetworkIdsEqual(id, trimmed))
                return true;
        }

        return false;
    }

    public void ClearRegisteredClients(int serverId) =>
        _registeredClients.TryRemove(serverId, out _);

    /// <summary>Known registered clients on a server (from the last GetClients snapshot). Unknown → treat as high load.</summary>
    public int GetRegisteredClientCount(int serverId)
    {
        if (!_registeredClients.TryGetValue(serverId, out var set))
            return int.MaxValue;
        return set.Count;
    }

    /// <summary>Trusted connections ordered for outbound delivery (trust, load, then rank stats).</summary>
    public IReadOnlyList<MessengerServerConnection> OrderConnectionsForDelivery(
        IEnumerable<MessengerServerConnection> connections) =>
        connections
            .Where(c => AllowsTraffic(c))
            .OrderBy(c => c, Comparer<MessengerServerConnection>.Create((a, b) =>
                MessengerServerDeliveryPreference.CompareConnections(
                    a, b, GetRankStatsRef, GetRegisteredClientCount)))
            .ThenBy(c => c.Entity.Id)
            .ToList();

    public void RecordRequestSuccess(int serverId)
    {
        var stats = _rankStats.GetOrAdd(serverId, _ => new MessengerServerRankStats());
        lock (stats)
        {
            stats.ConsecutiveFailures = 0;
            stats.LastSuccessUtc = DateTime.UtcNow;
        }
    }

    public void RecordRequestFailure(int serverId)
    {
        var stats = _rankStats.GetOrAdd(serverId, _ => new MessengerServerRankStats());
        lock (stats)
        {
            if (stats.ConsecutiveFailures < int.MaxValue)
                stats.ConsecutiveFailures++;
            stats.LastFailureUtc = DateTime.UtcNow;
        }
    }

    public void RecordProbeSuccess(int serverId, TimeSpan roundTrip) =>
        RecordKeepAliveSuccess(serverId, roundTrip);

    private void RecordKeepAliveSuccess(int serverId, TimeSpan roundTrip)
    {
        var stats = _rankStats.GetOrAdd(serverId, _ => new MessengerServerRankStats());
        var ms = Math.Max(0, (long)Math.Round(roundTrip.TotalMilliseconds));
        lock (stats)
        {
            stats.ConsecutiveFailures = 0;
            stats.LastSuccessUtc = DateTime.UtcNow;
            stats.LastKeepAliveRttMs = ms;
        }
    }

    private IReadOnlyList<MessengerServerConnection> SortConnectionsByRank(
        IEnumerable<MessengerServerConnection> connections) =>
        connections
            .OrderBy(c => GetRankStatsRef(c.Entity.Id),
                Comparer<MessengerServerRankStats>.Create(MessengerServerRankComparer.Compare))
            .ThenBy(c => c.Entity.Id)
            .ToList();

    private MessengerServerRankStats GetRankStatsRef(int serverId) =>
        _rankStats.GetOrAdd(serverId, _ => new MessengerServerRankStats());

    private static MessengerServerRankStats CloneStats(MessengerServerRankStats stats)
    {
        lock (stats)
        {
            return new MessengerServerRankStats
            {
                ConsecutiveFailures = stats.ConsecutiveFailures,
                LastKeepAliveRttMs = stats.LastKeepAliveRttMs,
                LastSuccessUtc = stats.LastSuccessUtc,
                LastFailureUtc = stats.LastFailureUtc
            };
        }
    }

    /// <summary>
    /// Fetches server certificate, stores fingerprint, registers (or marks existing), activates the server.
    /// </summary>
    public async Task<MessengerServerEntity> AddServerAsync(
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var user = RequireUser();
        var normalized = SqliteMessengerServerRepository.NormalizeBaseUrl(baseUrl);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var known = await _repository.ListByUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
            if (known.Any(s => MessengerServerQrCodec.EndpointsEqual(s.BaseUrl, normalized)))
                throw new InvalidOperationException("This server is already added.");

            var count = await _repository.CountByUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
            if (count >= MessengerServerLimits.MaxServersPerUser)
            {
                throw new InvalidOperationException(
                    $"A client can connect to at most {MessengerServerLimits.MaxServersPerUser} servers.");
            }

            await using var bootstrap = MessengerServerConnection.CreateBootstrap(normalized, DefaultHttpTimeout);
            var cert = await bootstrap.Api.GetServerCertificateAsync(cancellationToken).ConfigureAwait(false);
            var fingerprint = MessengerServerConnection.NormalizeFingerprint(cert.FingerprintSha256);
            if (string.IsNullOrEmpty(fingerprint))
                throw new InvalidOperationException("Server returned an empty certificate fingerprint.");

            var password = MessengerServerPasswordGenerator.Generate();
            var now = DateTime.UtcNow.Ticks;
            var entity = new MessengerServerEntity
            {
                UserId = user.Id,
                BaseUrl = normalized,
                FingerprintSha256 = fingerprint,
                Trusted = true,
                Active = true,
                IsRegistered = false,
                AccountPassword = password,
                NetworkId = user.NetworkIdShort.Trim(),
                Nick = user.Nickname.Trim(),
                TrustRating = TrustRatings.Default,
                CreatedUtcTicks = now,
                UpdatedUtcTicks = now
            };

            entity = await _repository.InsertAsync(entity, cancellationToken).ConfigureAwait(false);

            var connection = MessengerServerConnection.Create(entity, BlobHttpTimeout);
            _connections[entity.Id] = connection;

            try
            {
                await RegisterOrLoginAsync(connection, entity, user, cancellationToken).ConfigureAwait(false);
                await _repository.UpdateAsync(entity, cancellationToken).ConfigureAwait(false);
                connection.UpdateEntity(entity);
            }
            catch
            {
                _connections.TryRemove(entity.Id, out _);
                await connection.DisposeAsync().ConfigureAwait(false);
                await _repository.DeleteAsync(entity.Id, cancellationToken).ConfigureAwait(false);
                throw;
            }

            _logger.LogInformation("Messenger server added: {BaseUrl} (id={Id})", entity.BaseUrl, entity.Id);
            return entity;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Probes the server certificate. If reachable and the fingerprint still matches the pin,
    /// restores <see cref="MessengerServerEntity.Active"/> and <see cref="MessengerServerEntity.Trusted"/>.
    /// Unreachable servers are marked inactive (trust is kept). A fingerprint mismatch marks the server untrusted and inactive.
    /// </summary>
    public async Task<MessengerServerRecheckResult> RecheckServerAsync(
        int serverId,
        CancellationToken cancellationToken = default)
    {
        var user = RequireUser();
        MessengerServerEntity? needFailover = null;
        MessengerServerRecheckResult? result = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entity = await RequireOwnedServerAsync(user.Id, serverId, cancellationToken).ConfigureAwait(false);
            var expected = MessengerServerConnection.NormalizeFingerprint(entity.FingerprintSha256);

            await using var probe = MessengerServerConnection.CreateBootstrap(entity.BaseUrl, DefaultHttpTimeout);
            ServerCertificateResponse cert;
            try
            {
                cert = await probe.Api.GetServerCertificateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Messenger server recheck failed for {BaseUrl}", entity.BaseUrl);
                await PersistInactiveAsync(entity, cancellationToken).ConfigureAwait(false);
                return new MessengerServerRecheckResult
                {
                    Server = entity,
                    Status = MessengerServerRecheckStatus.Unreachable,
                    ExpectedFingerprint = expected,
                    ErrorMessage = ex.Message
                };
            }

            var actual = MessengerServerConnection.NormalizeFingerprint(cert.FingerprintSha256);
            if (!string.IsNullOrEmpty(actual) &&
                MessengerServerConnection.FingerprintsEqual(expected, actual))
            {
                entity.Trusted = true;
                entity.Active = true;
                await _repository.UpdateAsync(entity, cancellationToken).ConfigureAwait(false);
                if (_connections.TryGetValue(entity.Id, out var restored))
                    restored.UpdateEntity(entity);

                _logger.LogInformation(
                    "Messenger server recheck restored {BaseUrl} (id={Id}) as active and trusted.",
                    entity.BaseUrl,
                    entity.Id);

                return new MessengerServerRecheckResult
                {
                    Server = entity,
                    Status = MessengerServerRecheckStatus.AvailableAndTrusted,
                    ExpectedFingerprint = expected,
                    ActualFingerprint = actual
                };
            }

            await PersistUntrustedAsync(entity, cancellationToken).ConfigureAwait(false);
            await ReportClaimToPeersAsync(entity, ServerClaimReason.WRONGCERT, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogError(
                "Messenger server recheck mismatch for {BaseUrl}. Expected {Expected}, got {Actual}. Marked untrusted.",
                entity.BaseUrl,
                expected,
                actual);

            needFailover = entity;
            result = new MessengerServerRecheckResult
            {
                Server = entity,
                Status = MessengerServerRecheckStatus.FingerprintMismatch,
                ExpectedFingerprint = expected,
                ActualFingerprint = string.IsNullOrEmpty(actual) ? "(empty)" : actual
            };
        }
        finally
        {
            _gate.Release();
        }

        if (needFailover != null)
            await FailoverAfterUntrustAsync(needFailover, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Pings every active trusted server. Unreachable servers are marked inactive (trust kept);
    /// a TLS/fingerprint mismatch marks the server untrusted.
    /// </summary>
    public async Task PingActiveTrustedServersAsync(CancellationToken cancellationToken = default)
    {
        if (_auth.CurrentUser == null)
            return;

        var servers = await ListActiveTrustedAsync(cancellationToken).ConfigureAwait(false);
        if (servers.Count == 0)
            return;

#if NETFRAMEWORK
        await ParallelFx.ForEachAsync(
#else
        await Parallel.ForEachAsync(
#endif

            servers,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Clamp(servers.Count, 1, 8),
                CancellationToken = cancellationToken
            },
            async (server, ct) =>
            {
                try
                {
                    await PingServerAsync(server, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Inactive/untrusted — do not rewrite trust from a stale snapshot.
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Messenger server ping failed for {BaseUrl}", server.BaseUrl);
                    await PersistInactiveAsync(server, ct).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
    }

    public async Task SetActiveAsync(int serverId, bool active, CancellationToken cancellationToken = default)
    {
        var user = RequireUser();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entity = await RequireOwnedServerAsync(user.Id, serverId, cancellationToken).ConfigureAwait(false);
            if (active && !entity.Trusted)
            {
                throw new InvalidOperationException(
                    "Cannot activate an untrusted messenger server. Remove it and add again only if you trust the new certificate.");
            }

            entity.Active = active;
            await _repository.UpdateAsync(entity, cancellationToken).ConfigureAwait(false);
            if (_connections.TryGetValue(serverId, out var conn))
            {
                conn.UpdateEntity(entity);
                if (!active)
                    conn.ClearSession();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteServerAsync(int serverId, CancellationToken cancellationToken = default)
    {
        var user = RequireUser();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await RequireOwnedServerAsync(user.Id, serverId, cancellationToken).ConfigureAwait(false);
            if (_connections.TryRemove(serverId, out var conn))
                await conn.DisposeAsync().ConfigureAwait(false);
            _rankStats.TryRemove(serverId, out _);
            _registeredClients.TryRemove(serverId, out _);
            await _repository.DeleteAsync(serverId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Messenger server deleted: id={Id}", serverId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Verifies certificate fingerprint and ensures JWT (register or login). Must succeed before ChatRequest / presence.
    /// </summary>
    public async Task<MessengerServerConnection?> EnsureReadyAsync(
        MessengerServerEntity entity,
        CancellationToken cancellationToken = default)
    {
        Require.NotNull(entity);
        if (!AllowsTraffic(entity))
            return null;

        var user = RequireUser();
        MessengerServerConnection connection;
        try
        {
            connection = await GetOrCreateConnectionAsync(entity, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (!AllowsTraffic(connection))
            return null;

        if (!await VerifyCertificateOrMarkUntrustedAsync(connection, entity, cancellationToken).ConfigureAwait(false))
            return null;

        if (!AllowsTraffic(connection))
            return null;

        if (connection.HasValidToken)
        {
            await GossipAskRatingAsync(connection, entity, cancellationToken).ConfigureAwait(false);
            return connection;
        }

        await RegisterOrLoginAsync(connection, entity, user, cancellationToken).ConfigureAwait(false);
        await _repository.UpdateAsync(entity, cancellationToken).ConfigureAwait(false);
        connection.UpdateEntity(entity);
        await GossipAskRatingAsync(connection, entity, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public async Task<IReadOnlyList<MessengerServerConnection>> EnsureAllActiveReadyAsync(
        CancellationToken cancellationToken = default)
    {
        var servers = await ListActiveTrustedAsync(cancellationToken).ConfigureAwait(false);
        var ready = new List<MessengerServerConnection>(servers.Count);
        foreach (var server in servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var conn = await EnsureReadyAsync(server, cancellationToken).ConfigureAwait(false);
                if (conn != null)
                    ready.Add(conn);
                else
                    RecordRequestFailure(server.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordRequestFailure(server.Id);
                _logger.LogWarning(ex, "Failed to ready messenger server {BaseUrl}", server.BaseUrl);
            }
        }

        return SortConnectionsByRank(ready);
    }

    /// <summary>
    /// Asks <c>AskServers</c> on local peers with trust rating ≥ 0.3 (1–3 parallel calls),
    /// then upserts local rows: new servers above the floor, existing rows get the arithmetic mean.
    /// </summary>
    public async Task RefreshPeerTrustRatingsAsync(CancellationToken cancellationToken = default)
    {
        var user = _auth.CurrentUser;
        if (user == null)
            return;

        var all = await _repository.ListByUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var sources = all.Where(s => s.TrustRating >= TrustRatings.Floor && AllowsTraffic(s)).ToList();
        if (sources.Count == 0)
            return;

        var samples = new ConcurrentBag<RatedServer>();
        var workers = Clamp(sources.Count, 1, MaxAskServersWorkers);
#if NETFRAMEWORK
        await ParallelFx.ForEachAsync(
#else
        await Parallel.ForEachAsync(
#endif

            sources,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = workers,
                CancellationToken = cancellationToken
            },
            async (server, ct) =>
            {
                try
                {
                    var conn = await EnsureReadyAsync(server, ct).ConfigureAwait(false);
                    if (conn == null || !conn.HasValidToken)
                        return;
                    var list = await conn.Api.AskServersAsync(ct).ConfigureAwait(false);
                    foreach (var row in list)
                        samples.Add(new RatedServer(row.ServerIp, row.ServerPort, row.Rating));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "AskServers failed on {BaseUrl}", server.BaseUrl);
                }
            }).ConfigureAwait(false);

        var applied = await ApplyAskServersSamplesAsync(user.Id, samples, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Trust ratings refreshed from {Sources} peers, {Targets} endpoints ({Updated} updated, {Added} added)",
            sources.Count, applied.ReceivedCount, applied.UpdatedCount, applied.AddedCount);
    }

    /// <summary>
    /// Asks <c>AskServers</c> on one local peer (rating ≥ 0.3) and merges the returned ratings.
    /// </summary>
    public async Task<MessengerServerAskServersResult> AskServersFromAsync(
        int serverId,
        CancellationToken cancellationToken = default)
    {
        var user = RequireUser();
        var server = await RequireOwnedServerAsync(user.Id, serverId, cancellationToken).ConfigureAwait(false);
        if (server.TrustRating < TrustRatings.Floor)
            throw new InvalidOperationException("Рейтинг сервера ниже 0.3 — запрос списка недоступен.");

        var conn = await EnsureReadyAsync(server, cancellationToken).ConfigureAwait(false);
        if (conn == null || !conn.HasValidToken)
            throw new InvalidOperationException("Не удалось подключиться к серверу.");

        var list = await conn.Api.AskServersAsync(cancellationToken).ConfigureAwait(false);
        var samples = list.Select(row => new RatedServer(row.ServerIp, row.ServerPort, row.Rating)).ToList();
        var applied = await ApplyAskServersSamplesAsync(user.Id, samples, cancellationToken).ConfigureAwait(false);
        return applied with { ReceivedCount = list.Count };
    }

    private async Task<MessengerServerAskServersResult> ApplyAskServersSamplesAsync(
        int userId,
        IEnumerable<RatedServer> samples,
        CancellationToken cancellationToken)
    {
        var means = TrustRatings.AverageByEndpoint(samples);
        if (means.Count == 0)
            return new MessengerServerAskServersResult(0, 0, 0);

        var updated = 0;
        var added = 0;
        var latest = await _repository.ListByUserAsync(userId, cancellationToken).ConfigureAwait(false);
        foreach (var pair in means)
        {
            var endpoint = pair.Key;
            var mean = pair.Value;
            cancellationToken.ThrowIfCancellationRequested();
            var existing = latest.FirstOrDefault(s => MatchesEndpoint(s.BaseUrl, endpoint));
            if (existing != null)
            {
                existing.TrustRating = mean;
                await _repository.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
                if (_connections.TryGetValue(existing.Id, out var live))
                    live.UpdateEntity(existing);
                updated++;
                continue;
            }

            if (mean < TrustRatings.Floor)
                continue;

            var count = await _repository.CountByUserAsync(userId, cancellationToken).ConfigureAwait(false);
            if (count >= MessengerServerLimits.MaxServersPerUser)
            {
                _logger.LogDebug(
                    "Skip auto-add {Host}:{Port}: already {Count} servers",
                    endpoint.Host, endpoint.Port, count);
                continue;
            }

            try
            {
                var created = await AddServerAsync(ToHttpsBaseUrl(endpoint), cancellationToken)
                    .ConfigureAwait(false);
                created.TrustRating = mean;
                await _repository.UpdateAsync(created, cancellationToken).ConfigureAwait(false);
                latest = await _repository.ListByUserAsync(userId, cancellationToken).ConfigureAwait(false);
                added++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Auto-add messenger server {Host}:{Port} failed", endpoint.Host, endpoint.Port);
            }
        }

        return new MessengerServerAskServersResult(means.Count, updated, added);
    }

    private static bool MatchesEndpoint(string baseUrl, ServerEndpoint endpoint)
    {
        if (!TryParseEndpoint(baseUrl, out var host, out var port))
            return false;
        return ServerEndpoint.TryParse(host, port, out var local, out _) && local.EqualsEndpoint(endpoint);
    }

    private static string ToHttpsBaseUrl(ServerEndpoint endpoint)
    {
        var builder = new UriBuilder("https", endpoint.Host, endpoint.Port);
        return SqliteMessengerServerRepository.NormalizeBaseUrl(builder.Uri.GetLeftPart(UriPartial.Authority));
    }

    /// <summary>True when the client may connect, send, receive, or take presence from this server.</summary>
    public static bool AllowsTraffic(MessengerServerEntity entity) =>
        entity is { Active: true, Trusted: true };

    public bool AllowsTraffic(MessengerServerConnection connection) =>
        AllowsTraffic(connection.Entity);

    /// <summary>Ready connections that currently look available (no consecutive request failures).</summary>
    public IReadOnlyList<MessengerServerConnection> FilterAvailable(
        IEnumerable<MessengerServerConnection> connections) =>
        SortConnectionsByRank(connections.Where(c => AllowsTraffic(c) && IsServerAvailable(c.Entity.Id)));

    private async Task RegisterOrLoginAsync(
        MessengerServerConnection connection,
        MessengerServerEntity entity,
        UserEntity user,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(entity.AccountPassword) ||
            !MessengerServerPasswordGenerator.IsValid(entity.AccountPassword))
        {
            entity.AccountPassword = MessengerServerPasswordGenerator.Generate();
            entity.IsRegistered = false;
        }

        entity.NetworkId = user.NetworkIdShort.Trim();
        entity.Nick = user.Nickname.Trim();
        var deviceId = await _deviceId.GetDeviceIdAsync(cancellationToken).ConfigureAwait(false);

        if (!entity.IsRegistered)
        {
            try
            {
                await connection.Api.RegisterAsync(
                    new RegisterRequest
                    {
                        Nick = entity.Nick,
                        NetworkId = entity.NetworkId,
                        Password = entity.AccountPassword,
                        DeviceId = deviceId,
                        VisibleForSearch = VisibleForSearch
                    },
                    cancellationToken).ConfigureAwait(false);
                entity.IsRegistered = true;
            }
            catch (MessengerServerApiException ex) when (
                string.Equals(ex.ErrorCode, "Conflict", StringComparison.OrdinalIgnoreCase))
            {
                // Account already exists on server — fall through to login with stored password.
                entity.IsRegistered = true;
                _logger.LogInformation(
                    "Register conflict on {BaseUrl}; attempting login for {NetworkId}",
                    entity.BaseUrl,
                    entity.NetworkId);
            }
        }

        await connection.Api.LoginAsync(
            new LoginRequest
            {
                NetworkId = entity.NetworkId,
                Password = entity.AccountPassword,
                DeviceId = deviceId
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> VerifyCertificateOrMarkUntrustedAsync(
        MessengerServerConnection connection,
        MessengerServerEntity entity,
        CancellationToken cancellationToken)
    {
        ServerCertificateResponse? cert = null;
        try
        {
            cert = await connection.Api.GetServerCertificateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception pinnedEx)
        {
            _logger.LogWarning(pinnedEx, "Certificate fetch failed for {BaseUrl}", entity.BaseUrl);
            try
            {
                await using var probe = MessengerServerConnection.CreateBootstrap(entity.BaseUrl, DefaultHttpTimeout);
                cert = await probe.Api.GetServerCertificateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception unreachableEx)
            {
                _logger.LogWarning(unreachableEx, "Messenger server unreachable for {BaseUrl}", entity.BaseUrl);
                await PersistInactiveAsync(entity, cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        if (cert is null)
        {
            await PersistInactiveAsync(entity, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var actual = MessengerServerConnection.NormalizeFingerprint(cert.FingerprintSha256);
        var expected = MessengerServerConnection.NormalizeFingerprint(entity.FingerprintSha256);
        if (MessengerServerConnection.FingerprintsEqual(expected, actual))
            return true;

        await PersistUntrustedAsync(entity, cancellationToken).ConfigureAwait(false);
        await ReportClaimToPeersAsync(entity, ServerClaimReason.WRONGCERT, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogError(
            "Messenger server certificate mismatch for {BaseUrl}. Expected {Expected}, got {Actual}. Marked untrusted.",
            entity.BaseUrl,
            expected,
            actual);

        TrustThreatDetected?.Invoke(this,
            new MessengerServerTrustThreatEventArgs(entity, expected, actual));
        await FailoverAfterUntrustAsync(entity, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task PingServerAsync(MessengerServerEntity entity, CancellationToken cancellationToken)
    {
        if (!AllowsTraffic(entity))
            return;

        var connection = await GetOrCreateConnectionAsync(entity, cancellationToken).ConfigureAwait(false);
        if (!AllowsTraffic(connection))
            return;
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pingCts.CancelAfter(PingTimeout);
        var sw = Stopwatch.StartNew();
        try
        {
            await connection.Api.PingAsync(pingCts.Token).ConfigureAwait(false);
            sw.Stop();
            RecordKeepAliveSuccess(entity.Id, sw.Elapsed);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception pingEx)
        {
            _logger.LogWarning(pingEx, "Ping failed for {BaseUrl}", entity.BaseUrl);
        }

        // Ping timed out / failed. Probe certificate on a separate client so we do not
        // tear down a healthy long-poll session on a transient /tech/ping blip.
        try
        {
            await using var probe = MessengerServerConnection.CreateBootstrap(entity.BaseUrl, PingTimeout);
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(PingTimeout);
            var cert = await probe.Api.GetServerCertificateAsync(probeCts.Token).ConfigureAwait(false);
            var actual = MessengerServerConnection.NormalizeFingerprint(cert.FingerprintSha256);
            var expected = MessengerServerConnection.NormalizeFingerprint(entity.FingerprintSha256);
            if (!MessengerServerConnection.FingerprintsEqual(expected, actual))
            {
                await PersistUntrustedAsync(entity, cancellationToken).ConfigureAwait(false);
                await ReportClaimToPeersAsync(entity, ServerClaimReason.WRONGCERT, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogError(
                    "Messenger server ping mismatch for {BaseUrl}. Expected {Expected}, got {Actual}. Marked untrusted.",
                    entity.BaseUrl,
                    expected,
                    actual);
                TrustThreatDetected?.Invoke(this,
                    new MessengerServerTrustThreatEventArgs(entity, expected, actual));
                await FailoverAfterUntrustAsync(entity, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Host answered /certificate with the pinned fingerprint — keep Active.
            // A single failed Ping must not ClearSession / kill the long-poll waiter.
            RecordKeepAliveSuccess(entity.Id, PingTimeout);
            _logger.LogDebug(
                "Messenger server ping failed but certificate ok for {BaseUrl}; keeping active",
                entity.BaseUrl);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception unreachableEx)
        {
            _logger.LogWarning(unreachableEx, "Messenger server unreachable for {BaseUrl}", entity.BaseUrl);
        }

        await PersistInactiveAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Server is down / does not ping: deactivate, keep trusted.</summary>
    private async Task PersistInactiveAsync(MessengerServerEntity entity, CancellationToken cancellationToken)
    {
        var latest = await _repository.GetByIdAsync(entity.Id, cancellationToken).ConfigureAwait(false);
        if (latest == null || !latest.Trusted)
        {
            entity.Active = false;
            entity.Trusted = false;
            return;
        }

        if (!latest.Active)
        {
            entity.Active = false;
            entity.Trusted = true;
            return;
        }

        latest.Active = false;
        await _repository.UpdateAsync(latest, cancellationToken).ConfigureAwait(false);
        entity.Active = false;
        entity.Trusted = latest.Trusted;
        if (_connections.TryGetValue(entity.Id, out var conn))
        {
            conn.UpdateEntity(entity);
            conn.ClearSession();
        }

        await ReportClaimToPeersAsync(entity, ServerClaimReason.UNAVAILABLE, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Fingerprint mismatch: untrusted and inactive. Drops the live HTTP session.</summary>
    private async Task PersistUntrustedAsync(MessengerServerEntity entity, CancellationToken cancellationToken)
    {
        entity.Trusted = false;
        entity.Active = false;
        await _repository.UpdateAsync(entity, cancellationToken).ConfigureAwait(false);
        await DropConnectionAsync(entity).ConfigureAwait(false);
        _registeredClients.TryRemove(entity.Id, out _);
        _logger.LogWarning("Dropped connection to untrusted messenger server {BaseUrl} (id={Id})", entity.BaseUrl, entity.Id);
    }

    private async Task<MessengerServerFailoverEventArgs> FailoverAfterUntrustAsync(
        MessengerServerEntity untrusted,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _failoverBusy, 1, 0) != 0)
            return new MessengerServerFailoverEventArgs(untrusted, null, switchedToMesh: false);

        try
        {
            var user = RequireUser();
            var all = await _repository.ListByUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
            var trusted = all.Where(s => s.Trusted && s.Id != untrusted.Id).ToList();
            if (trusted.Count == 0)
            {
                var mesh = new MessengerServerFailoverEventArgs(untrusted, null, switchedToMesh: true);
                _logger.LogWarning("No remaining trusted messenger servers; switching to mesh.");
                FailoverCompleted?.Invoke(this, mesh);
                return mesh;
            }

            var ordered = SortServersByRank(trusted);
            foreach (var candidate in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!candidate.Active)
                    {
                        candidate.Active = true;
                        await _repository.UpdateAsync(candidate, cancellationToken).ConfigureAwait(false);
                    }

                    var conn = await EnsureReadyAsync(candidate, cancellationToken).ConfigureAwait(false);
                    if (conn == null)
                        continue;

                    _logger.LogInformation(
                        "Messenger server failover: {From} → {To}",
                        untrusted.BaseUrl,
                        candidate.BaseUrl);
                    var switched = new MessengerServerFailoverEventArgs(untrusted, candidate, switchedToMesh: false);
                    FailoverCompleted?.Invoke(this, switched);
                    return switched;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failover candidate {BaseUrl} is not ready", candidate.BaseUrl);
                }
            }

            var meshFallback = new MessengerServerFailoverEventArgs(untrusted, null, switchedToMesh: true);
            _logger.LogWarning("No available trusted messenger server after untrust; switching to mesh.");
            FailoverCompleted?.Invoke(this, meshFallback);
            return meshFallback;
        }
        finally
        {
            Interlocked.Exchange(ref _failoverBusy, 0);
        }
    }

    private IReadOnlyList<MessengerServerEntity> SortServersByRank(IEnumerable<MessengerServerEntity> servers) =>
        servers
            .OrderByDescending(s => s.Active)
            .ThenBy(s => IsServerAvailable(s.Id) ? 0 : 1)
            .ThenBy(s => GetRankStatsRef(s.Id),
                Comparer<MessengerServerRankStats>.Create(MessengerServerRankComparer.Compare))
            .ThenBy(s => s.Id)
            .ToList();

    private async Task DropConnectionAsync(MessengerServerEntity entity)
    {
        if (!_connections.TryRemove(entity.Id, out var conn))
            return;

        try
        {
            conn.UpdateEntity(entity);
            conn.ClearSession();
            await conn.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // already torn down
        }
    }

    private async Task<MessengerServerConnection> GetOrCreateConnectionAsync(
        MessengerServerEntity entity,
        CancellationToken cancellationToken)
    {
        var latest = await _repository.GetByIdAsync(entity.Id, cancellationToken).ConfigureAwait(false);
        if (latest == null || !AllowsTraffic(latest))
            throw new InvalidOperationException("Cannot connect to an inactive or untrusted messenger server.");

        entity.Trusted = latest.Trusted;
        entity.Active = latest.Active;

        if (_connections.TryGetValue(entity.Id, out var existing))
        {
            if (!AllowsTraffic(existing))
                throw new InvalidOperationException("Cannot connect to an inactive or untrusted messenger server.");
            existing.UpdateEntity(entity);
            return existing;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connections.TryGetValue(entity.Id, out existing))
            {
                if (!AllowsTraffic(existing))
                    throw new InvalidOperationException("Cannot connect to an inactive or untrusted messenger server.");
                existing.UpdateEntity(entity);
                return existing;
            }

            if (!AllowsTraffic(entity))
                throw new InvalidOperationException("Cannot connect to an inactive or untrusted messenger server.");

            var created = MessengerServerConnection.Create(entity, BlobHttpTimeout);
            _connections[entity.Id] = created;
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MessengerServerEntity> RequireOwnedServerAsync(
        int userId,
        int serverId,
        CancellationToken cancellationToken)
    {
        var entity = await _repository.GetByIdAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (entity == null || entity.UserId != userId)
            throw new InvalidOperationException("Messenger server not found.");
        return entity;
    }

    private async Task GossipAskRatingAsync(
        MessengerServerConnection connection,
        MessengerServerEntity self,
        CancellationToken cancellationToken)
    {
        if (!connection.HasValidToken || !AllowsTraffic(connection))
            return;

        IReadOnlyList<MessengerServerEntity> others;
        try
        {
            var user = RequireUser();
            var all = await _repository.ListByUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
            others = all.Where(s => s.Id != self.Id).ToList();
        }
        catch
        {
            return;
        }

        foreach (var other in others)
        {
            if (!TryParseEndpoint(other.BaseUrl, out var ip, out var port))
                continue;
            try
            {
                await connection.Api.AskRatingAsync(ip, port, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "AskRating failed on {BaseUrl} for {Peer}", self.BaseUrl, other.BaseUrl);
            }
        }
    }

    private async Task ReportClaimToPeersAsync(
        MessengerServerEntity target,
        ServerClaimReason reason,
        CancellationToken cancellationToken)
    {
        if (!TryParseEndpoint(target.BaseUrl, out var ip, out var port))
            return;

        IReadOnlyList<MessengerServerEntity> peers;
        try
        {
            var user = RequireUser();
            var all = await _repository.ListByUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
            peers = all.Where(s => s.Id != target.Id && s.Trusted && s.Active).ToList();
        }
        catch
        {
            return;
        }

        foreach (var peer in peers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!_connections.TryGetValue(peer.Id, out var conn) || !conn.HasValidToken || !AllowsTraffic(conn))
                    continue;
                await conn.Api.ClaimServerAsync(ip, port, reason, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "ClaimServer {Reason} for {Target} sent to {Peer}",
                    reason, target.BaseUrl, peer.BaseUrl);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "ClaimServer {Reason} to {Peer} failed", reason, peer.BaseUrl);
            }
        }
    }

    internal static bool TryParseEndpoint(string? baseUrl, out string host, out int port)
    {
        host = "";
        port = 0;
        if (!Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out var uri))
            return false;
#if NETCOREAPP
        host = uri.IdnHost;
        if (string.IsNullOrWhiteSpace(host))
            host = uri.Host;
#else
        host = uri.Host;
#endif
        port = uri.IsDefaultPort
            ? uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80
            : uri.Port;
        return host.Length > 0 && port is >= 1 and <= 65535;
    }

    private UserEntity RequireUser() =>
        _auth.CurrentUser ?? throw new InvalidOperationException("Not logged in.");

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _connections.Keys.ToArray())
        {
            if (_connections.TryRemove(id, out var conn))
                await conn.DisposeAsync().ConfigureAwait(false);
        }

        _registeredClients.Clear();
        _gate.Dispose();
    }

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;
}
