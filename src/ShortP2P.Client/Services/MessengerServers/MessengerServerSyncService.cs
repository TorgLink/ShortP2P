using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShortP2P.Auth;
using ShortP2P.Auth.Data;
using ShortP2P.Client.ChatMedia;
using ShortP2P.Client.Data;
using ShortP2P.Crypto;
using ShortP2P.Discovery;
using ShortP2P.Discovery.Gossip;
using ShortP2P.Discovery.Profile;
using ShortP2P.MessengerServer.Contracts.Dtos;
using ShortP2P.MessengerServer.Http;

namespace ShortP2P.Client.Services.MessengerServers;

/// <summary>
/// Background long-poll inbox for messenger servers, plus outbound server-first message delivery.
/// </summary>
public sealed class MessengerServerSyncService : IAsyncDisposable
{
    /// <summary>Default long-poll wait requested from the server (seconds).</summary>
    public const int LongPollTimeoutSeconds = 25;

    /// <summary>Hard cap for a single outbound SendMessage HTTP call (must stay below HttpClient blob timeout).</summary>
    public static readonly TimeSpan SendMessageHttpTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Max concurrent outbound SendMessage calls (and long-poll workers).</summary>
    public const int MaxLongPollWorkers = 3;

    /// <summary>Alias: max parallel fan-out when posting a message to joined servers.</summary>
    public const int MaxSendWorkers = MaxLongPollWorkers;

    private readonly AuthService _auth;
    private readonly ChatRepository _chats;
    private readonly ChatSessionCache _sessions;
    private readonly MessengerServerManager _manager;
    private readonly IBotVersionRepository? _botVersions;
    private readonly IPeerProfileStore? _peerProfiles;
    private readonly ILocalPeerProfileSource? _localProfile;
    private readonly ILogger<MessengerServerSyncService> _logger;
    private readonly ServerCipherSessionStore _serverSessions;
    private readonly object _startGate = new();
    private readonly SemaphoreSlim _ingestGate = new(1, 1);
    private readonly ConcurrentDictionary<long, string> _pendingForwardProfileNonces = new();

    /// <summary>SHO-10: peer network id → messenger server id chosen after UDP establish failed.</summary>
    private readonly ConcurrentDictionary<string, int> _preferredServerByPeer = new(StringComparer.Ordinal);

    /// <summary>Base URLs that answered 404 on bot routes. Bot downloads must not ask them.</summary>
    private readonly ConcurrentDictionary<string, byte> _serversWithoutBotApi = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _longPollLoop;
    private Task? _botPollLoop;
    private Task? _pingLoop;
    private Task? _ratingLoop;
    private bool _started;

    public MessengerServerSyncService(
        AuthService auth,
        ChatRepository chats,
        ChatSessionCache sessions,
        MessengerServerManager manager,
        IBotVersionRepository? botVersions = null,
        IPeerProfileStore? peerProfiles = null,
        ILocalPeerProfileSource? localProfile = null,
        ILogger<MessengerServerSyncService>? logger = null,
        ServerCipherSessionStore? serverSessions = null)
    {
        _auth = auth ?? throw new global::System.ArgumentNullException(nameof(auth));
        _chats = chats ?? throw new global::System.ArgumentNullException(nameof(chats));
        _sessions = sessions ?? throw new global::System.ArgumentNullException(nameof(sessions));
        _manager = manager ?? throw new global::System.ArgumentNullException(nameof(manager));
        _botVersions = botVersions;
        _peerProfiles = peerProfiles;
        _localProfile = localProfile;
        _logger = logger ?? NullLogger<MessengerServerSyncService>.Instance;
        _serverSessions = serverSessions ?? ServerCipherSessionStore.Shared;
    }

    /// <summary>Optional: refresh LAN scan AboutMe after a server-forward profile reply.</summary>
    public Action<CompressedNetworkId, string>? PeerAboutMeApplied { get; set; }

    public MessengerServerManager Manager => _manager;

    public void Start()
    {
        lock (_startGate)
        {
            if (_started)
                return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _manager.FailoverCompleted -= OnFailoverCompleted;
            _manager.FailoverCompleted += OnFailoverCompleted;
            _longPollLoop = Task.Run(() => LongPollSupervisorAsync(token), token);
            _botPollLoop = Task.Run(() => BotLongPollSupervisorAsync(token), token);
            _pingLoop = Task.Run(() => PingLoopAsync(token), token);
            _ratingLoop = Task.Run(() => TrustRatingLoopAsync(token), token);
            _started = true;
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? longPoll;
        Task? botPoll;
        Task? pingLoop;
        Task? ratingLoop;
        lock (_startGate)
        {
            if (!_started)
                return;
            _started = false;
            cts = _cts;
            longPoll = _longPollLoop;
            botPoll = _botPollLoop;
            pingLoop = _pingLoop;
            ratingLoop = _ratingLoop;
            _cts = null;
            _longPollLoop = null;
            _botPollLoop = null;
            _pingLoop = null;
            _ratingLoop = null;
            _manager.FailoverCompleted -= OnFailoverCompleted;
        }

        if (cts != null)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        await WaitIgnoreCancelAsync(longPoll).ConfigureAwait(false);
        await WaitIgnoreCancelAsync(botPoll).ConfigureAwait(false);
        await WaitIgnoreCancelAsync(pingLoop).ConfigureAwait(false);
        await WaitIgnoreCancelAsync(ratingLoop).ConfigureAwait(false);
        cts?.Dispose();
    }

    /// <summary>Discovery priority hook: auth + GetClients before UDP/BT.</summary>
    public Task RunDiscoveryRoundAsync(CancellationToken cancellationToken) =>
        ProbeAndListRemoteClientsAsync(cancellationToken);

    private async Task PingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _manager.PingActiveTrustedServersAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Messenger server ping round failed");
            }

            try
            {
                await Task.Delay(MessengerServerManager.PingPeriod, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TrustRatingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _manager.RefreshPeerTrustRatingsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Messenger server trust-rating round failed");
            }

            try
            {
                await Task.Delay(MessengerServerManager.RatingRefreshPeriod, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<IReadOnlyList<ClientPresenceDto>> ProbeAndListRemoteClientsAsync(
        CancellationToken cancellationToken)
    {
        var user = _auth.CurrentUser;
        if (user == null)
            return [];

        var self = user.NetworkIdShort.Trim();
        var ready = await _manager.EnsureAllActiveReadyAsync(cancellationToken).ConfigureAwait(false);

        return await ListRemoteClientsAsync(ready, self, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GET /bots on each ready trusted server. Failures of one server are skipped.</summary>
    public async Task<IReadOnlyList<BotCatalogDto>> ProbeAndListRemoteBotsAsync(
        CancellationToken cancellationToken)
    {
        var user = _auth.CurrentUser;
        if (user == null)
            return [];

        var self = user.NetworkIdShort.Trim();
        var ready = await _manager.EnsureAllActiveReadyAsync(cancellationToken).ConfigureAwait(false);
        return await ListRemoteBotsAsync(ready, self, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// GET /bots/{id}/commands on ready trusted servers.
    /// Returns the first non-empty list found for this bot network id.
    /// </summary>
    public async Task<IReadOnlyList<BotCommandDto>> FetchBotCommandsAsync(
        string botNetworkId,
        CancellationToken cancellationToken = default)
    {
        var id = ChatRepository.CanonicalPeerNetworkId(botNetworkId);
        if (id.Length == 0)
            return [];

        var ready = await _manager.EnsureAllActiveReadyAsync(cancellationToken).ConfigureAwait(false);
        foreach (var conn in ready)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_manager.AllowsTraffic(conn))
                continue;

            try
            {
                var reported = await conn.Api.GetBotCommandsAsync(id, cancellationToken).ConfigureAwait(false);
                var reportedId = ChatRepository.CanonicalPeerNetworkId(reported.BotNetworkId);
                if (reportedId.Length == 0 ||
                    !ChatRepository.PeerNetworkIdsEqual(reportedId, id))
                    continue;

                return reported.Commands ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "GetBotCommands failed for {BotId} on {BaseUrl}", id, conn.Entity.BaseUrl);
            }
        }

        return [];
    }

    /// <summary>
    /// Saved bot versions keyed by canonical network id.
    /// The same bot on several servers is one string of every version and endpoint.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> DescribeBotVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_botVersions == null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var rows = await _botVersions.ListAllAsync(cancellationToken).ConfigureAwait(false);
        var grouped = new Dictionary<string, List<BotVersionPlace>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var id = ChatRepository.CanonicalPeerNetworkId(row.BotNetworkId);
            if (id.Length == 0 || row.Version < 0)
                continue;
            var endpoint = row.Endpoint?.Trim() ?? "";
            if (endpoint.Length == 0)
                continue;
            if (!grouped.TryGetValue(id, out var list))
            {
                list = [];
                grouped[id] = list;
            }

            list.Add(new BotVersionPlace(row.Version, endpoint));
        }

        var result = new Dictionary<string, string>(grouped.Count, StringComparer.Ordinal);
        foreach (var pair in grouped)
        {
            var text = BotVersionText.Format(pair.Value);
            if (text.Length > 0)
                result[pair.Key] = text;
        }

        return result;
    }

    /// <summary>Directory rows for <see cref="LocalNetworkScanner.ApplyBotDirectory"/>.</summary>
    public static MessengerServerDirectoryEntry[] ToBotDirectoryEntries(IReadOnlyList<BotCatalogDto> bots)
    {
        var list = new MessengerServerDirectoryEntry[bots.Count];
        for (var i = 0; i < bots.Count; i++)
        {
            var bot = bots[i];
            var readable = bot.BotReadableName?.Trim() ?? "";
            var name = bot.BotName?.Trim() ?? "";
            var nick = readable.Length > 0 ? readable : name;
            list[i] = new MessengerServerDirectoryEntry(
                bot.NetworkId.Trim(),
                nick,
                false,
                DateTimeOffset.UtcNow,
                true,
                bot.BotDescription?.Trim() ?? "",
                bot.MessagePublicKey?.Trim() ?? "");
        }

        return list;
    }

    /// <summary>Refresh PeerRsaPublicJson / IsBot on existing chats from GET /bots.</summary>
    public async Task SyncBotKeysFromCatalogAsync(
        IReadOnlyList<BotCatalogDto> bots,
        CancellationToken cancellationToken = default)
    {
        var user = _auth.CurrentUser;
        if (user == null || bots.Count == 0)
            return;

        var applied = 0;
        var skippedNoKey = 0;
        foreach (var bot in bots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = bot.NetworkId.Trim();
            var pub = bot.MessagePublicKey?.Trim() ?? "";
            if (id.Length == 0)
                continue;
            if (pub.Length == 0)
            {
                skippedNoKey++;
                continue;
            }

            var chat = await FindChatForBotCatalogAsync(user.Id, bot).ConfigureAwait(false);
            if (chat == null)
                continue;
            if (await _chats.TryApplyBotCatalogKeyAsync(chat.Id, pub, id).ConfigureAwait(false))
                applied++;
        }

        _logger.LogDebug(
            "SyncBotKeysFromCatalog: bots={BotCount} applied={Applied} skippedNoKey={SkippedNoKey}",
            bots.Count, applied, skippedNoKey);
    }

    /// <summary>
    /// True when outbound still needs a successful GET /bots classification for this chat row.
    /// </summary>
    public static bool NeedsBotCatalogLookup(ChatEntity chat)
    {
        if (chat.IsBot)
            return string.IsNullOrWhiteSpace(chat.PeerRsaPublicJson);
        return !chat.BotCatalogChecked;
    }

    /// <summary>
    /// If this chat's peer is listed in GET /bots, mark IsBot and store MessagePublicKey.
    /// On a successful catalog response with no match, persist <see cref="ChatEntity.BotCatalogChecked"/>
    /// so human chats do not re-probe on every send. Failed GET /bots does not set the flag.
    /// </summary>
    public async Task<ChatEntity?> TryEnsureBotChatFromCatalogAsync(
        int chatId,
        CancellationToken cancellationToken = default)
    {
        var user = _auth.CurrentUser;
        if (user == null)
            return null;

        var chat = await _chats.GetChatAsync(chatId).ConfigureAwait(false);
        if (chat == null)
            return null;

        if (chat.IsBot && !string.IsNullOrWhiteSpace(chat.PeerRsaPublicJson))
        {
            if (!chat.BotCatalogChecked)
            {
                await _chats.TryMarkBotCatalogCheckedAsync(chat.Id).ConfigureAwait(false);
                return await _chats.GetChatAsync(chat.Id).ConfigureAwait(false) ?? chat;
            }

            return chat;
        }

        // Already classified as not-a-bot after a successful catalog probe.
        if (!chat.IsBot && chat.BotCatalogChecked)
            return chat;

        IReadOnlyList<BotCatalogDto> bots;
        try
        {
            bots = await ProbeAndListRemoteBotsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TryEnsureBotChatFromCatalog: GetBots failed for chat {ChatId}", chatId);
            return null;
        }

        var bot = FindBotCatalogMatch(bots, chat);
        if (bot == null)
        {
            await _chats.TryMarkBotCatalogCheckedAsync(chat.Id).ConfigureAwait(false);
            var human = await _chats.GetChatAsync(chat.Id).ConfigureAwait(false);
            _logger.LogDebug(
                "TryEnsureBotChatFromCatalog: peer {PeerId} not in bot catalog (bots={Count}) chat={ChatId} — marked BotCatalogChecked",
                chat.PeerNetworkIdShort, bots.Count, chatId);
            return human ?? chat;
        }

        var pub = bot.MessagePublicKey?.Trim() ?? "";
        if (pub.Length == 0)
        {
            _logger.LogError(
                "TryEnsureBotChatFromCatalog: bot {BotId} ({BotName}) has empty MessagePublicKey — re-register the bot",
                bot.NetworkId, bot.BotName);
            return null;
        }

        await _chats.TryApplyBotCatalogKeyAsync(chat.Id, pub, bot.NetworkId).ConfigureAwait(false);
        var fresh = await _chats.GetChatAsync(chat.Id).ConfigureAwait(false);
        _logger.LogDebug(
            "TryEnsureBotChatFromCatalog: upgraded chat {ChatId} peer={PeerId} IsBot={IsBot} hasKey={HasKey}",
            chatId,
            fresh?.PeerNetworkIdShort ?? chat.PeerNetworkIdShort,
            fresh?.IsBot ?? false,
            !string.IsNullOrWhiteSpace(fresh?.PeerRsaPublicJson));
        return fresh;
    }

    private async Task<ChatEntity?> FindChatForBotCatalogAsync(int userId, BotCatalogDto bot)
    {
        var byNetworkId = await _chats.FindChatByPeerNetworkIdAsync(userId, bot.NetworkId).ConfigureAwait(false);
        if (byNetworkId != null)
            return byNetworkId;

        // Chats sometimes store BotName as PeerNetworkIdShort when the bot registered with a
        // human-readable network id that later changed, or was added manually.
        var botName = bot.BotName?.Trim() ?? "";
        if (botName.Length == 0 ||
            ChatRepository.PeerNetworkIdsEqual(botName, bot.NetworkId))
            return null;

        return await _chats.FindChatByPeerNetworkIdAsync(userId, botName).ConfigureAwait(false);
    }

    private static BotCatalogDto? FindBotCatalogMatch(IReadOnlyList<BotCatalogDto> bots, ChatEntity chat)
    {
        var peer = chat.PeerNetworkIdShort?.Trim() ?? "";
        var nick = chat.PeerNickname?.Trim() ?? "";
        foreach (var bot in bots)
        {
            var id = bot.NetworkId?.Trim() ?? "";
            var name = bot.BotName?.Trim() ?? "";
            if (id.Length > 0 && ChatRepository.PeerNetworkIdsEqual(id, peer))
                return bot;
            if (name.Length > 0 &&
                (string.Equals(name, peer, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, nick, StringComparison.OrdinalIgnoreCase)))
                return bot;
        }

        return null;
    }

    /// <summary>Obsolete name kept for call sites; prefers GetClients probe.</summary>
    public Task<IReadOnlyList<ClientPresenceDto>> KeepAliveAndListRemoteClientsAsync(
        CancellationToken cancellationToken) =>
        ProbeAndListRemoteClientsAsync(cancellationToken);

    /// <summary>
    /// Best-effort: send PeerProfileRequest (0x44) via POST /forward when peer is reachable on a trusted server.
    /// </summary>
    public async Task RequestPeerProfileViaForwardAsync(
        CompressedNetworkId peerNetworkId,
        CancellationToken cancellationToken = default)
    {
        var user = _auth.CurrentUser;
        if (user == null || peerNetworkId.IsEmpty)
            return;

        var self = user.NetworkIdShort.Trim();
        var peerId = peerNetworkId.ToShortString();
        if (string.IsNullOrEmpty(peerId) ||
            string.Equals(peerId, self, StringComparison.Ordinal))
            return;

#if NETFRAMEWORK
        long nonce;
        {
            var bytes = new byte[8];
            new Random().NextBytes(bytes);
            nonce = BitConverter.ToInt64(bytes, 0);
        }
#else
        var nonce = Random.Shared.NextInt64();
#endif
        _pendingForwardProfileNonces[nonce] = peerId;

        byte[] requestBytes;
        try
        {
            requestBytes = PeerProfileWireCodec.BuildRequest(nonce, CompressedNetworkId.FromShortString(self));
        }
        catch (Exception ex)
        {
            _pendingForwardProfileNonces.TryRemove(nonce, out _);
            _logger.LogWarning(ex, "Peer profile forward: failed to build 0x44 for {PeerId}", peerId);
            return;
        }

        var forwardId = Guid.NewGuid().ToString("N");
        var dto = new ForwardRequest
        {
            ForwardId = forwardId,
            SrcNetworkId = self,
            TgtNetworkId = peerId,
            Kind = ForwardKind.PeerProfileRequest,
            PayloadBase64 = Convert.ToBase64String(requestBytes)
        };

        try
        {
            var accepted = await TryPostForwardAsync(dto, peerId, cancellationToken).ConfigureAwait(false);
            if (!accepted)
                _pendingForwardProfileNonces.TryRemove(nonce, out _);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _pendingForwardProfileNonces.TryRemove(nonce, out _);
            _logger.LogWarning(ex, "Peer profile forward request failed for {PeerId} (best-effort)", peerId);
        }
    }

    /// <summary>
    ///     One trusted server in preference order (healthier first).
    ///     <see cref="SubscriberPresent"/> is registration on that server; online is preferred when several match.
    /// </summary>
    public readonly record struct HostingServerCandidate(int ServerId, bool SubscriberPresent, bool SubscriberOnline);

    /// <summary>
    ///     Picks the server that actually lists the subscriber. Online beats offline.
    ///     Among the same presence, the earlier entry wins (caller passes rank order).
    /// </summary>
    public static int? SelectHostingServerId(IReadOnlyList<HostingServerCandidate> rankedByPreference)
    {
        Require.NotNull(rankedByPreference);
        int? presentOffline = null;
        foreach (var candidate in rankedByPreference)
        {
            if (!candidate.SubscriberPresent)
                continue;
            if (candidate.SubscriberOnline)
                return candidate.ServerId;
            presentOffline ??= candidate.ServerId;
        }

        return presentOffline;
    }

    /// <summary>
    ///     SHO-10 workaround. Lists available trusted servers, keeps the one that has
    ///     <paramref name="peerNetworkId"/>, republishes ChatRequest (invite) there, and pins later
    ///     sends to that server. Receive stays on the existing long-poll, which already includes it.
    /// </summary>
    public async Task<MessengerServerEntity?> TrySwitchPeerToHostingServerAsync(
        string peerNetworkId,
        CancellationToken cancellationToken = default)
    {
        var user = _auth.CurrentUser;
        if (user == null)
            return null;

        var peerId = ChatRepository.CanonicalPeerNetworkId(peerNetworkId);
        if (peerId.Length == 0 ||
            string.Equals(peerId, ChatRepository.CanonicalPeerNetworkId(user.NetworkIdShort), StringComparison.Ordinal))
            return null;

        var ready = await _manager.EnsureAllActiveReadyAsync(cancellationToken).ConfigureAwait(false);
        var ordered = _manager.OrderConnectionsForDelivery(ready);
        if (ordered.Count == 0)
        {
            _logger.LogInformation(
                "UDP fallback: no available messenger servers while looking for peer {PeerId}", peerId);
            return null;
        }

        MessengerServerConnection? chosen = null;
        MessengerServerConnection? presentOffline = null;
        foreach (var conn in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var presence = await FindPeerPresenceAsync(conn, peerId, cancellationToken).ConfigureAwait(false);
            if (presence == null)
                continue;
            if (presence.IsOnline)
            {
                chosen = conn;
                break;
            }

            presentOffline ??= conn;
        }

        chosen ??= presentOffline;
        if (chosen == null)
        {
            _logger.LogInformation(
                "UDP fallback: peer {PeerId} is not registered on any available messenger server", peerId);
            return null;
        }

        _preferredServerByPeer[peerId] = chosen.Entity.Id;
        await PublishChatRequestOnAsync(chosen, peerId, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "UDP fallback: peer {PeerId} switched to messenger server {BaseUrl}",
            peerId, chosen.Entity.BaseUrl);
        return chosen.Entity;
    }

    /// <summary>Drops the SHO-10 server pin so the next send is not forced onto one messenger server.</summary>
    public void ClearPreferredServer(string peerNetworkId)
    {
        var peerId = ChatRepository.CanonicalPeerNetworkId(peerNetworkId);
        if (peerId.Length == 0)
            return;
        _preferredServerByPeer.TryRemove(peerId, out _);
    }

    /// <summary>
    ///     Base URL of the SHO-10 / Auto server pin for this peer, when still known on a live connection.
    /// </summary>
    public bool TryGetPreferredServerBaseUrl(string peerNetworkId, out string? baseUrl)
    {
        baseUrl = null;
        var peerId = ChatRepository.CanonicalPeerNetworkId(peerNetworkId);
        if (peerId.Length == 0)
            return false;
        if (!_preferredServerByPeer.TryGetValue(peerId, out var serverId))
            return false;
        return _manager.TryGetServerBaseUrl(serverId, out baseUrl);
    }

    /// <summary>Publish (or refresh) our public key to the peer via all ready servers.</summary>
    public async Task PublishChatRequestAsync(string targetNetworkId, CancellationToken cancellationToken = default)
    {
        var user = _auth.CurrentUser;
        if (user == null)
            return;

        var publicKey = RsaKeySerializer.SerializePublic(_auth.GetCurrentPublicKey());
        var target = targetNetworkId.Trim();
        if (string.IsNullOrEmpty(target) ||
            string.Equals(target, user.NetworkIdShort.Trim(), StringComparison.Ordinal))
            return;

        var ready = await _manager.EnsureAllActiveReadyAsync(cancellationToken).ConfigureAwait(false);
        foreach (var conn in ready)
        {
            if (!_manager.AllowsTraffic(conn))
                continue;
            await PublishChatRequestOnAsync(conn, target, cancellationToken, publicKey).ConfigureAwait(false);
        }
    }

    private async Task PublishChatRequestOnAsync(
        MessengerServerConnection connection,
        string targetNetworkId,
        CancellationToken cancellationToken,
        string? publicKey = null)
    {
        if (!_manager.AllowsTraffic(connection))
            return;

        publicKey ??= RsaKeySerializer.SerializePublic(_auth.GetCurrentPublicKey());
        try
        {
            LogChatRequest("send", connection, targetNetworkId);
            await TrackAsync(connection, () => connection.Api.CreateChatRequestAsync(
                new ChatRequestCreateRequest
                {
                    PublicKey = publicKey,
                    TargetNetworkId = targetNetworkId
                },
                cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var (host, port) = SplitServerHostPort(connection.Entity.BaseUrl);
            _logger.LogWarning(ex, "CreateChatRequest failed on {Host}:{Port} ({BaseUrl})",
                host, port, connection.Entity.BaseUrl);
        }
    }

    /// <summary>
    /// Wait briefly for the background long-poll to apply ChatRequest keys — never call
    /// <c>PollEvents</c> here. A second waiter used to replace the long-poll waiter on older
    /// servers and still races <c>TakeForDevice</c> (destructive) on current ones.
    /// </summary>
    public async Task DrainInboxOnceAsync(CancellationToken cancellationToken = default)
    {
        // No-op network poll. Callers re-read the chat row after this returns; keys arrive via
        // LongPollServerLoopAsync. Keep a short yield so PublishChatRequest + long-poll can land.
        try
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    /// <summary>
    /// Delivers an already-built chat wire through trusted messenger servers where the peer is registered.
    /// Returns the server <c>MessageId</c> if at least one such server accepted the message; otherwise null.
    /// </summary>
    public async Task<string?> TryDeliverWireAsync(
        ChatEntity chat,
        UserEntity user,
        byte[] wire,
        CancellationToken cancellationToken = default)
    {
        Require.NotNull(chat);
        Require.NotNull(user);
        Require.NotNull(wire);

        var latest = await _chats.GetChatAsync(chat.Id).ConfigureAwait(false) ?? chat;
        _logger.LogDebug(
            "TryDeliverWire: chat={ChatId} peer={PeerId} IsBot={IsBot} wireLen={WireLen} hasPeerKey={HasKey}",
            latest.Id,
            latest.PeerNetworkIdShort,
            latest.IsBot,
            wire.Length,
            !string.IsNullOrWhiteSpace(latest.PeerRsaPublicJson));

        if (string.IsNullOrWhiteSpace(latest.PeerRsaPublicJson))
        {
            // Do not PollEvents — long-poll owns the inbox waiter. Re-publish ChatRequest and
            // give the background poll a moment to write PeerRsaPublicJson locally.
            try
            {
                await PublishChatRequestAsync(latest.PeerNetworkIdShort, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "PublishChatRequest before send failed for chat {ChatId}", latest.Id);
            }

            await DrainInboxOnceAsync(cancellationToken).ConfigureAwait(false);
            latest = await _chats.GetChatAsync(chat.Id).ConfigureAwait(false) ?? latest;
        }

        if (string.IsNullOrWhiteSpace(latest.PeerRsaPublicJson))
        {
            _logger.LogDebug("Send skipped: no peer public key for chat {ChatId}", latest.Id);
            return null;
        }

        RsaPublicKey peerKey;
        try
        {
            peerKey = RsaKeySerializer.DeserializePublic(latest.PeerRsaPublicJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize peer public key for chat {ChatId}", latest.Id);
            return null;
        }

        if (latest.IsBot)
        {
            _logger.LogDebug(
                "Send skipped: chat {ChatId} is a bot — use TryDeliverBotWireAsync",
                latest.Id);
            return null;
        }

        var peerId = ChatRepository.CanonicalPeerNetworkId(latest.PeerNetworkIdShort);
        if (peerId.Length == 0)
            return null;

        string encrypted;
        try
        {
            encrypted = Convert.ToBase64String(
                MessengerServerPayloadCodec.EncryptForSession(wire, peerKey, peerId, _serverSessions));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt server payload for chat {ChatId}", latest.Id);
            return null;
        }

        var targets = await CollectPeerServerTargetsAsync(peerId, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            _logger.LogDebug(
                "Send skipped: no ready trusted messenger server for peer {PeerId}",
                peerId);
            return null;
        }

        var now = DateTime.UtcNow;
        var messageId = Guid.NewGuid().ToString("N");
        var dto = new MessageDto
        {
            MessageId = messageId,
            SrcNetworkId = user.NetworkIdShort.Trim(),
            TgtNetworkId = peerId,
            CreatedUtc = now,
            UpdatedUtc = now,
            EncryptedDataBase64 = encrypted
        };

        foreach (var conn in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_manager.AllowsTraffic(conn))
                continue;
            try
            {
                _logger.LogDebug(
                    "SendMessage POST messageId={MessageId} peer={PeerId} on {BaseUrl}",
                    messageId, peerId, conn.Entity.BaseUrl);
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sendCts.CancelAfter(SendMessageHttpTimeout);
                await TrackAsync(conn, () => conn.Api.SendMessageAsync(dto, sendCts.Token))
                    .ConfigureAwait(false);
                _logger.LogDebug(
                    "SendMessage OK messageId={MessageId} peer={PeerId} on {BaseUrl}",
                    messageId, peerId, conn.Entity.BaseUrl);
                return messageId;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("SendMessage timed out on {BaseUrl}", conn.Entity.BaseUrl);
                _manager.RecordRequestFailure(conn.Entity.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "SendMessage failed on {BaseUrl}", conn.Entity.BaseUrl);
            }
        }

        _logger.LogError(
            "SendMessage: all targets failed for chat {ChatId} peer={PeerId} tried={Count}",
            latest.Id, peerId, targets.Count);
        return null;
    }

    /// <summary>
    /// Posts an already-built session envelope (probe acknowledgement or re-key notice). Not chat history.
    /// </summary>
    public async Task TryDeliverRawEnvelopeAsync(
        string peerNetworkId,
        UserEntity user,
        byte[] envelope,
        CancellationToken cancellationToken = default)
    {
        Require.NotNull(user);
        Require.NotNull(envelope);
        var peerId = ChatRepository.CanonicalPeerNetworkId(peerNetworkId);
        if (peerId.Length == 0 || envelope.Length == 0)
            return;

        var targets = await CollectPeerServerTargetsAsync(peerId, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            _logger.LogDebug("Session control skipped: no ready server for peer {PeerId}", peerId);
            return;
        }

        var now = DateTime.UtcNow;
        var messageId = Guid.NewGuid().ToString("N");
        var dto = new MessageDto
        {
            MessageId = messageId,
            SrcNetworkId = user.NetworkIdShort.Trim(),
            TgtNetworkId = peerId,
            CreatedUtc = now,
            UpdatedUtc = now,
            EncryptedDataBase64 = Convert.ToBase64String(envelope)
        };

        foreach (var conn in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_manager.AllowsTraffic(conn))
                continue;
            try
            {
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sendCts.CancelAfter(SendMessageHttpTimeout);
                await TrackAsync(conn, () => conn.Api.SendMessageAsync(dto, sendCts.Token))
                    .ConfigureAwait(false);
                _logger.LogDebug(
                    "Session control posted messageId={MessageId} peer={PeerId} on {BaseUrl}",
                    messageId, peerId, conn.Entity.BaseUrl);
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Session control timed out on {BaseUrl}", conn.Entity.BaseUrl);
                _manager.RecordRequestFailure(conn.Entity.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Session control failed on {BaseUrl}", conn.Entity.BaseUrl);
            }
        }
    }

    /// <summary>
    /// Bot chats only: encrypt ChatWire with the bot catalog RSA key and POST bot_client_interaction/send_message.
    /// </summary>
    public async Task<string?> TryDeliverBotWireAsync(
        ChatEntity chat,
        UserEntity user,
        byte[] wire,
        CancellationToken cancellationToken = default,
        string? serverMessageId = null)
    {
        Require.NotNull(chat);
        Require.NotNull(user);
        Require.NotNull(wire);

        var latest = await _chats.GetChatAsync(chat.Id).ConfigureAwait(false) ?? chat;
        _logger.LogDebug(
            "TryDeliverBotWire: chat={ChatId} bot={BotId} IsBot={IsBot} wireLen={WireLen} hasBotKey={HasKey}",
            latest.Id,
            latest.PeerNetworkIdShort,
            latest.IsBot,
            wire.Length,
            !string.IsNullOrWhiteSpace(latest.PeerRsaPublicJson));

        if (!latest.IsBot)
        {
            _logger.LogDebug("Bot send skipped: chat {ChatId} is not a bot", latest.Id);
            return null;
        }

        if (string.IsNullOrWhiteSpace(latest.PeerRsaPublicJson))
        {
            _logger.LogError("Bot send skipped: no bot public key for chat {ChatId}", latest.Id);
            return null;
        }

        // Publish our public key so the bot can encrypt replies (crypto_keys src=us tgt=bot).
        try
        {
            await PublishChatRequestAsync(latest.PeerNetworkIdShort, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "PublishChatRequest before bot send failed for chat {ChatId}", latest.Id);
        }

        RsaPublicKey botKey;
        try
        {
            botKey = RsaKeySerializer.DeserializePublic(latest.PeerRsaPublicJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize bot public key for chat {ChatId}", latest.Id);
            return null;
        }

        string encrypted;
        try
        {
            encrypted = MessengerServerPayloadCodec.EncryptToBase64(wire, botKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt bot payload for chat {ChatId}", latest.Id);
            return null;
        }

        var botId = ChatRepository.CanonicalPeerNetworkId(latest.PeerNetworkIdShort);
        if (botId.Length == 0)
            return null;

        var ready = await _manager.EnsureAllActiveReadyAsync(cancellationToken).ConfigureAwait(false);
        var targets = _manager.OrderConnectionsForDelivery(ready)
            .Where(c => _manager.AllowsTraffic(c))
            .ToArray();
        if (targets.Length == 0)
        {
            _logger.LogError("Bot send skipped: no ready trusted messenger server for bot {BotId}", botId);
            return null;
        }

        var messageId = string.IsNullOrWhiteSpace(serverMessageId)
            ? Guid.NewGuid().ToString("N")
            : serverMessageId.Trim();
        var request = new BotSendMessageRequest
        {
            MessageId = messageId,
            NetworkId = botId,
            DateTime = DateTime.UtcNow,
            Message = encrypted
        };

        foreach (var conn in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_manager.AllowsTraffic(conn))
                continue;
            try
            {
                _logger.LogDebug(
                    "SendMessageToBot POST messageId={MessageId} bot={BotId} on {BaseUrl}",
                    messageId, botId, conn.Entity.BaseUrl);
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sendCts.CancelAfter(SendMessageHttpTimeout);
                var response = await TrackAsync(
                        conn,
                        () => conn.Api.SendMessageToBotAsync(request, sendCts.Token))
                    .ConfigureAwait(false);
                if (response.ErrorCode != ErrorCode.Success)
                {
                    _logger.LogError(
                        "SendMessageToBot ErrorCode={Code} on {BaseUrl}: {Message}",
                        response.ErrorCode,
                        conn.Entity.BaseUrl,
                        response.ErrorMessage);
                    continue;
                }

                var accepted = string.IsNullOrWhiteSpace(response.MessageId)
                    ? messageId
                    : response.MessageId.Trim();
                _logger.LogDebug(
                    "SendMessageToBot OK messageId={MessageId} bot={BotId} on {BaseUrl}",
                    accepted, botId, conn.Entity.BaseUrl);
                return accepted;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("SendMessageToBot timed out on {BaseUrl}", conn.Entity.BaseUrl);
                _manager.RecordRequestFailure(conn.Entity.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "SendMessageToBot failed on {BaseUrl}", conn.Entity.BaseUrl);
            }
        }

        _logger.LogError(
            "SendMessageToBot: all targets failed for chat {ChatId} bot={BotId} tried={Count}",
            latest.Id, botId, targets.Length);
        return null;
    }

    /// <summary>
    /// Encrypts an inner chat-wire (image/file frame) and PUTs it to trusted servers where the peer is registered.
    /// Returns true if at least one server accepted the blob.
    /// </summary>
    public async Task<bool> TryUploadBlobAsync(
        ChatEntity chat,
        UserEntity user,
        string blobId,
        byte[] innerWire,
        CancellationToken cancellationToken = default)
    {
        Require.NotNull(chat);
        Require.NotNull(user);
        Require.NotNullOrWhiteSpace(blobId);
        Require.NotNull(innerWire);

        if (string.IsNullOrWhiteSpace(chat.PeerRsaPublicJson))
            return false;

        RsaPublicKey peerKey;
        try
        {
            peerKey = RsaKeySerializer.DeserializePublic(chat.PeerRsaPublicJson);
        }
        catch
        {
            return false;
        }

        var peerId = ChatRepository.CanonicalPeerNetworkId(chat.PeerNetworkIdShort);
        if (peerId.Length == 0)
            return false;

        byte[] ciphertext;
        try
        {
            ciphertext = MessengerServerPayloadCodec.EncryptPreferSession(
                innerWire, peerKey, peerId, _serverSessions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to encrypt blob for chat {ChatId}", chat.Id);
            return false;
        }

        var targets = await CollectPeerServerTargetsAsync(peerId, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
            return false;

        foreach (var conn in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_manager.AllowsTraffic(conn))
                continue;
            try
            {
                using var putCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                putCts.CancelAfter(TimeSpan.FromMinutes(2));
                await TrackAsync(conn, () => conn.Api.PutBlobAsync(blobId, peerId, ciphertext, putCts.Token))
                    .ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("PutBlob timed out on {BaseUrl}", conn.Entity.BaseUrl);
                _manager.RecordRequestFailure(conn.Entity.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "PutBlob failed on {BaseUrl}", conn.Entity.BaseUrl);
            }
        }

        return false;
    }

    /// <summary>
    /// Encrypts an inner chat-wire and uploads it in 50 KB chunks to a ready trusted server.
    /// Returns null when every server rejected the upload.
    /// </summary>
    public async Task<BotChunkUploadResult?> TryUploadBotChunksAsync(
        ChatEntity chat,
        UserEntity user,
        string blobId,
        byte[] innerWire,
        CancellationToken cancellationToken = default)
    {
        Require.NotNull(chat);
        Require.NotNull(user);
        Require.NotNullOrWhiteSpace(blobId);
        Require.NotNull(innerWire);

        if (string.IsNullOrWhiteSpace(chat.PeerRsaPublicJson))
            return null;

        RsaPublicKey peerKey;
        try
        {
            peerKey = RsaKeySerializer.DeserializePublic(chat.PeerRsaPublicJson);
        }
        catch
        {
            return null;
        }

        byte[] ciphertext;
        try
        {
            ciphertext = MessengerServerPayloadCodec.Encrypt(innerWire, peerKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to encrypt bot blob for chat {ChatId}", chat.Id);
            return null;
        }

        var peerId = chat.PeerNetworkIdShort.Trim();
        if (peerId.Length == 0)
            return null;

        var chunks = BotBlobChunker.Split(ciphertext);
        var sha = BotBlobChunker.Sha256Hex(ciphertext);
        var ready = await _manager.EnsureAllActiveReadyAsync(cancellationToken).ConfigureAwait(false);
        var targets = _manager.OrderConnectionsForDelivery(ready)
            .Where(c => _manager.AllowsTraffic(c))
            .ToArray();

        foreach (var conn in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uploaded = true;
            for (var i = 0; i < chunks.Length; i++)
            {
                var index = i;
                try
                {
                    await BotChunkRetry.RunAsync(
                            ct => TrackAsync(
                                conn,
                                () => conn.Api.PutBlobChunkAsync(
                                    blobId,
                                    index,
                                    peerId,
                                    chunks.Length,
                                    ciphertext.Length,
                                    chunks[index],
                                    ct)),
                            ex => ex is not MessengerServerApiException api || (int)api.StatusCode != 409,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Bot chunk {Index} upload failed on {BaseUrl}", index, conn.Entity.BaseUrl);
                    uploaded = false;
                    break;
                }
            }

            if (uploaded)
                return new BotChunkUploadResult(conn.Entity.BaseUrl, chunks.Length, sha);
        }

        return null;
    }

    /// <summary>
    /// Downloads ciphertext chunks and checks the SHA-256 from the transfer offer.
    /// </summary>
    public async Task<byte[]?> TryDownloadBotChunksAsync(
        string blobId,
        string? preferredBaseUrl,
        int totalChunks,
        string? ciphertextSha256,
        CancellationToken cancellationToken = default)
    {
        Require.NotNullOrWhiteSpace(blobId);

        var ready = await _manager.EnsureAllActiveReadyAsync(cancellationToken).ConfigureAwait(false);
        var ordered = OrderConnectionsForBlobDownload(ready, preferredBaseUrl)
            .Where(SupportsBotApi)
            .ToArray();
        foreach (var conn in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_manager.AllowsTraffic(conn))
                continue;

            try
            {
                var manifest = await CallDownloadAsync(
                        conn,
                        ct => conn.Api.GetBlobChunkManifestAsync(blobId, ct),
                        cancellationToken)
                    .ConfigureAwait(false);
                var expected = totalChunks >= 1 ? totalChunks : manifest.TotalChunks;
                if (expected < 1 || manifest.TotalChunks != expected || manifest.Indexes == null ||
                    manifest.Indexes.Length < expected)
                {
                    _logger.LogDebug(
                        "Bot blob {BlobId} on {BaseUrl} has {Actual} chunk(s), expected {Expected}",
                        blobId,
                        conn.Entity.BaseUrl,
                        manifest.TotalChunks,
                        expected);
                    continue;
                }

                var parts = new byte[expected][];
                var complete = true;
                for (var i = 0; i < expected; i++)
                {
                    var index = i;
                    try
                    {
                        parts[i] = await BotChunkRetry.RunAsync(
                                ct => CallDownloadAsync(
                                    conn,
                                    token => conn.Api.GetBlobChunkAsync(blobId, index, token),
                                    ct),
                                ex => ex is not MessengerServerApiException api || (int)api.StatusCode != 404,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Bot chunk {Index} download failed on {BaseUrl}", index, conn.Entity.BaseUrl);
                        complete = false;
                        break;
                    }
                }

                if (!complete)
                    continue;

                var joined = BotBlobChunker.Join(parts);
                if (joined.Length != manifest.TotalBytes)
                    continue;
                if (!string.IsNullOrWhiteSpace(ciphertextSha256) &&
                    !string.Equals(BotBlobChunker.Sha256Hex(joined), ciphertextSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return joined;
            }
            catch (MessengerServerApiException ex) when ((int)ex.StatusCode == 404)
            {
                _logger.LogDebug("Bot blob {BlobId} not found on {BaseUrl}", blobId, conn.Entity.BaseUrl);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Bot blob download failed on {BaseUrl}", conn.Entity.BaseUrl);
            }
        }

        return null;
    }

    /// <summary>Client receipt for a bot → client message. Deletes the server copy and its chunks.</summary>
    public async Task TrySubmitBotMessageReceiptAsync(
        string messageId,
        string? preferredBaseUrl,
        CancellationToken cancellationToken = default)
    {
        Require.NotNullOrWhiteSpace(messageId);
        var ready = await _manager.EnsureAllActiveReadyAsync(cancellationToken).ConfigureAwait(false);
        var ordered = OrderConnectionsForBlobDownload(ready, preferredBaseUrl)
            .Where(SupportsBotApi)
            .ToArray();
        foreach (var conn in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_manager.AllowsTraffic(conn))
                continue;
            try
            {
                await CallDownloadAsync(
                        conn,
                        async ct =>
                        {
                            await conn.Api.SubmitBotMessageReceiptAsync(
                                    new BotMessageReceiptRequest
                                    {
                                        MessageId = messageId.Trim(),
                                        ReceivedAtUtc = DateTime.UtcNow
                                    },
                                    ct)
                                .ConfigureAwait(false);
                            return true;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (MessengerServerApiException ex) when ((int)ex.StatusCode == 404)
            {
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Bot message receipt failed on {BaseUrl}", conn.Entity.BaseUrl);
            }
        }
    }

    /// <summary>
    /// GETs an opaque blob from trusted servers. Tries <paramref name="preferredBaseUrl"/> first, then rank order.
    /// Returns null if every server responded 404 or failed.
    /// </summary>
    public async Task<byte[]?> TryDownloadBlobAsync(
        string blobId,
        string? preferredBaseUrl,
        CancellationToken cancellationToken = default,
        bool botServersOnly = false)
    {
        Require.NotNullOrWhiteSpace(blobId);

        var ready = await _manager.EnsureAllActiveReadyAsync(cancellationToken).ConfigureAwait(false);
        var ordered = OrderConnectionsForBlobDownload(ready, preferredBaseUrl);
        if (botServersOnly)
            ordered = ordered.Where(SupportsBotApi).ToList();
        if (ordered.Count == 0)
            return null;

        foreach (var conn in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_manager.AllowsTraffic(conn))
                continue;

            try
            {
                var bytes = await CallDownloadAsync(
                        conn,
                        ct => conn.Api.GetBlobAsync(blobId, ct),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                    return bytes;
            }
            catch (MessengerServerApiException ex) when ((int)ex.StatusCode == 404)
            {
                _logger.LogDebug("Blob {BlobId} not found on {BaseUrl}", blobId, conn.Entity.BaseUrl);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "GetBlob failed on {BaseUrl}", conn.Entity.BaseUrl);
            }
        }

        return null;
    }

    /// <summary>
    /// Removes the blob from trusted servers after the recipient accepted it.
    /// Failures are ignored: TTL retention still applies.
    /// </summary>
    public async Task TryDeleteBlobAsync(string blobId, CancellationToken cancellationToken = default)
    {
        Require.NotNullOrWhiteSpace(blobId);

        var ready = await _manager.EnsureAllActiveReadyAsync(cancellationToken).ConfigureAwait(false);
        var targets = ready.Where(c => _manager.AllowsTraffic(c)).ToArray();
        if (targets.Length == 0)
            return;

        var degree = Clamp(targets.Length, 1, MaxSendWorkers);
#if NETFRAMEWORK
        await ParallelFx.ForEachAsync(
#else
        await Parallel.ForEachAsync(
#endif

            targets,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = degree,
                CancellationToken = cancellationToken
            },
            async (conn, ct) =>
            {
                if (!_manager.AllowsTraffic(conn))
                    return;
                try
                {
                    await TrackAsync(conn, () => conn.Api.DeleteBlobAsync(blobId, ct)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "DeleteBlob failed on {BaseUrl}", conn.Entity.BaseUrl);
                }
            }).ConfigureAwait(false);
    }

    private async Task<List<MessengerServerConnection>> CollectPeerServerTargetsAsync(
        string peerId,
        CancellationToken cancellationToken)
    {
        var ready = await _manager.EnsureAllActiveReadyAsync(cancellationToken).ConfigureAwait(false);
        var ordered = _manager.OrderConnectionsForDelivery(ready);
        if (ordered.Count == 0)
            return new List<MessengerServerConnection>();

        var peerKey = ChatRepository.CanonicalPeerNetworkId(peerId);
        if (_preferredServerByPeer.TryGetValue(peerKey, out var preferredId))
        {
            var preferred = ordered.FirstOrDefault(c => c.Entity.Id == preferredId);
            if (preferred != null)
            {
                ordered = PromoteConnectionToFront(ordered, preferred);
                if (_manager.IsClientRegisteredOnServer(preferred.Entity.Id, peerId) ||
                    await PeerRegisteredOnTrustedServerAsync(preferred, peerId, cancellationToken)
                        .ConfigureAwait(false))
                    return new List<MessengerServerConnection> { preferred };
            }
        }

        var cachedRegistered = ordered
            .Where(c => _manager.IsClientRegisteredOnServer(c.Entity.Id, peerId))
            .ToList();
        if (cachedRegistered.Count > 0)
            return cachedRegistered;

        foreach (var conn in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await PeerRegisteredOnTrustedServerAsync(conn, peerId, cancellationToken).ConfigureAwait(false))
                return new List<MessengerServerConnection> { conn };
        }

        // GetClients can fail or use a different id spelling; SendMessage still stores for the target id.
        return ordered.ToList();
    }

    private static List<MessengerServerConnection> PromoteConnectionToFront(
        IReadOnlyList<MessengerServerConnection> ordered,
        MessengerServerConnection preferred)
    {
        var list = new List<MessengerServerConnection>(ordered.Count) { preferred };
        foreach (var conn in ordered)
        {
            if (!ReferenceEquals(conn, preferred) && conn.Entity.Id != preferred.Entity.Id)
                list.Add(conn);
        }

        return list;
    }

    private List<MessengerServerConnection> OrderConnectionsForBlobDownload(
        IReadOnlyList<MessengerServerConnection> ready,
        string? preferredBaseUrl)
    {
        var ordered = new List<MessengerServerConnection>(ready.Count);
        if (preferredBaseUrl is not null && !string.IsNullOrWhiteSpace(preferredBaseUrl))
        {
            var hint = SqliteMessengerServerRepository.NormalizeBaseUrl(preferredBaseUrl);
            var hinted = ready.FirstOrDefault(c =>
                _manager.AllowsTraffic(c) &&
                string.Equals(
                    SqliteMessengerServerRepository.NormalizeBaseUrl(c.Entity.BaseUrl),
                    hint,
                    StringComparison.OrdinalIgnoreCase));
            if (hinted != null)
                ordered.Add(hinted);
        }

        foreach (var conn in _manager.OrderConnectionsForDelivery(ready))
        {
            if (!ordered.Contains(conn))
                ordered.Add(conn);
        }

        return ordered;
    }

    private async Task<IReadOnlyList<ClientPresenceDto>> ListRemoteClientsAsync(
        IReadOnlyList<MessengerServerConnection> ready,
        string selfNetworkId,
        CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, ClientPresenceDto>(StringComparer.Ordinal);
        foreach (var conn in ready)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_manager.AllowsTraffic(conn))
                continue;
            IReadOnlyList<ClientPresenceDto> list;
            var sw = Stopwatch.StartNew();
            try
            {
                list = await TrackAsync(conn, () => conn.Api.GetClientsAsync(cancellationToken))
                    .ConfigureAwait(false);
                sw.Stop();
                _manager.RecordProbeSuccess(conn.Entity.Id, sw.Elapsed);
                _manager.ReplaceRegisteredClients(conn.Entity.Id, list.Select(c => c.NetworkId));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "GetClients failed on {BaseUrl}", conn.Entity.BaseUrl);
                continue;
            }

            foreach (var client in list)
            {
                var id = client.NetworkId.Trim();
                if (id.Length == 0 ||
                    string.Equals(id, selfNetworkId, StringComparison.Ordinal))
                    continue;

                if (!byId.TryGetValue(id, out var existing) || PreferClient(client, existing))
                    byId[id] = client;
            }
        }

        return byId.Values.ToArray();
    }

    private async Task<IReadOnlyList<BotCatalogDto>> ListRemoteBotsAsync(
        IReadOnlyList<MessengerServerConnection> ready,
        string selfNetworkId,
        CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, BotCatalogDto>(StringComparer.Ordinal);
        foreach (var conn in ready)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_manager.AllowsTraffic(conn))
                continue;

            IReadOnlyList<BotCatalogDto> list;
            try
            {
                // Not TrackAsync: a missing /bots on an older server must not count as a trust failure.
                list = await conn.Api.GetBotsAsync(cancellationToken).ConfigureAwait(false);
                MarkBotApiAvailable(conn.Entity.BaseUrl);
            }
            catch (MessengerServerApiException ex) when ((int)ex.StatusCode == 404)
            {
                if (MarkBotApiUnavailable(conn.Entity.BaseUrl))
                    _logger.LogDebug("GetBots is not available on {BaseUrl}", conn.Entity.BaseUrl);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "GetBots failed on {BaseUrl}", conn.Entity.BaseUrl);
                continue;
            }

            foreach (var bot in list)
            {
                var id = bot.NetworkId.Trim();
                if (id.Length == 0 || string.Equals(id, selfNetworkId, StringComparison.Ordinal))
                    continue;
                if (!byId.ContainsKey(id))
                    byId[id] = bot;
            }

            await SaveBotVersionsAsync(conn, list, cancellationToken).ConfigureAwait(false);
        }

        return byId.Values.ToArray();
    }

    private async Task SaveBotVersionsAsync(
        MessengerServerConnection conn,
        IReadOnlyList<BotCatalogDto> bots,
        CancellationToken cancellationToken)
    {
        if (_botVersions == null || bots.Count == 0)
            return;

        var server = conn.Entity;
        string endpoint;
        try
        {
            endpoint = BotVersionText.EndpointFromBaseUrl(server.BaseUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Bot version save skipped; base URL is invalid on server {ServerId}", server.Id);
            return;
        }

        var fingerprint = MessengerServerConnection.NormalizeFingerprint(server.FingerprintSha256);
        foreach (var bot in bots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = bot.NetworkId?.Trim() ?? "";
            if (id.Length == 0)
                continue;

            try
            {
                var reported = await conn.Api.GetBotVersionAsync(id, cancellationToken).ConfigureAwait(false);
                var reportedId = ChatRepository.CanonicalPeerNetworkId(reported.BotNetworkId);
                if (reportedId.Length == 0 || reported.Version < 0)
                    continue;

                await _botVersions.UpsertAsync(
                    new BotVersionEntity
                    {
                        ServerId = server.Id,
                        BotNetworkId = reportedId,
                        Version = reported.Version,
                        Endpoint = endpoint,
                        FingerprintSha256 = fingerprint,
                        UpdatedUtcTicks = DateTime.UtcNow.Ticks
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "GetBotVersion failed for {BotId} on {BaseUrl}", id, server.BaseUrl);
            }
        }
    }

    private async Task<ClientPresenceDto?> FindPeerPresenceAsync(
        MessengerServerConnection connection,
        string peerNetworkId,
        CancellationToken cancellationToken)
    {
        if (!_manager.AllowsTraffic(connection))
            return null;

        try
        {
            var list = await TrackAsync(connection, () => connection.Api.GetClientsAsync(cancellationToken))
                .ConfigureAwait(false);
            _manager.ReplaceRegisteredClients(connection.Entity.Id, list.Select(c => c.NetworkId));
            ClientPresenceDto? match = null;
            foreach (var client in list)
            {
                if (!ChatRepository.PeerNetworkIdsEqual(client.NetworkId, peerNetworkId))
                    continue;
                if (match == null || PreferClient(client, match))
                    match = client;
            }

            return match;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "GetClients failed while looking up {PeerId} on {BaseUrl}",
                peerNetworkId, connection.Entity.BaseUrl);
            return null;
        }
    }

    private async Task<bool> PeerRegisteredOnTrustedServerAsync(
        MessengerServerConnection connection,
        string peerNetworkId,
        CancellationToken cancellationToken)
    {
        if (!_manager.AllowsTraffic(connection))
            return false;

        if (_manager.IsClientRegisteredOnServer(connection.Entity.Id, peerNetworkId))
            return true;

        try
        {
            var list = await TrackAsync(connection, () => connection.Api.GetClientsAsync(cancellationToken))
                .ConfigureAwait(false);
            _manager.ReplaceRegisteredClients(connection.Entity.Id, list.Select(c => c.NetworkId));
            return _manager.IsClientRegisteredOnServer(connection.Entity.Id, peerNetworkId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "GetClients failed while checking registration on {BaseUrl}", connection.Entity.BaseUrl);
            return false;
        }
    }

    private static bool PreferClient(ClientPresenceDto candidate, ClientPresenceDto existing)
    {
        var candidateOnline = candidate.IsOnline;
        var existingOnline = existing.IsOnline;
        if (candidateOnline != existingOnline)
            return candidateOnline;
        return candidate.LastSeenAtUtc > existing.LastSeenAtUtc;
    }


    private async Task LongPollSupervisorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var user = _auth.CurrentUser;
                if (user == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var ready = await _manager.EnsureAllActiveReadyAsync(cancellationToken).ConfigureAwait(false);
                // Prefer trusted/active servers even after transient HTTP failures — long-poll
                // itself is the health signal. FilterAvailable would pause inbox for seconds.
                var pollTargets = ready.Where(c => _manager.AllowsTraffic(c)).ToArray();
                if (pollTargets.Length == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var degree = Clamp(pollTargets.Length, 1, MaxLongPollWorkers);
                var selected = pollTargets.Take(degree).ToArray();
                var workers = selected
                    .Select(conn => LongPollServerLoopAsync(conn, user, cancellationToken))
                    .ToArray();

                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Messenger server long-poll supervisor failed");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task BotLongPollSupervisorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var user = _auth.CurrentUser;
                if (user == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var ready = await _manager.EnsureAllActiveReadyAsync(cancellationToken).ConfigureAwait(false);
                var pollTargets = ready.Where(c => _manager.AllowsTraffic(c)).ToArray();
                if (pollTargets.Length == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var degree = Clamp(pollTargets.Length, 1, MaxLongPollWorkers);
                var selected = pollTargets.Take(degree).ToArray();
                var workers = selected
                    .Select(conn => BotLongPollServerLoopAsync(conn, user, cancellationToken))
                    .ToArray();

                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Messenger server bot long-poll supervisor failed");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task BotLongPollServerLoopAsync(
        MessengerServerConnection connection,
        UserEntity user,
        CancellationToken cancellationToken)
    {
        // Unacked attachment offers stay in the inbox until the file is downloaded.
        // The server returns that set immediately, so an unchanged inbox must wait
        // instead of spinning decrypt + SQLite and starving the download.
        var heldBackoff = TimeSpan.FromSeconds(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_manager.AllowsTraffic(connection))
                break;

            BotWaitForMessagesResponse inbox;
            try
            {
                try
                {
                    // Not TrackAsync on 404: an older server without bot routes must not
                    // accumulate consecutive failures and drop out of the trusted set.
                    inbox = await connection.LongPollApi.WaitForBotMessagesAsync(
                            new BotWaitForMessagesRequest
                            {
                                NetworkId = user.NetworkIdShort.Trim(),
                                TimeoutSeconds = LongPollTimeoutSeconds
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (MessengerServerApiException ex) when ((int)ex.StatusCode == 404)
                {
                    if (MarkBotApiUnavailable(connection.Entity.BaseUrl))
                    {
                        _logger.LogDebug(
                            "Bot wait_for_messages is not available on {BaseUrl} (404)",
                            connection.Entity.BaseUrl);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!_manager.AllowsTraffic(connection))
                    break;

                _manager.RecordRequestSuccess(connection.Entity.Id);
                MarkBotApiAvailable(connection.Entity.BaseUrl);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _manager.RecordRequestFailure(connection.Entity.Id);
                _logger.LogError(ex, "Bot wait_for_messages failed on {BaseUrl}", connection.Entity.BaseUrl);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            try
            {
                if (inbox.Messages is { Count: > 0 })
                {
                    _logger.LogDebug(
                        "Bot wait_for_messages: {Count} message(s) on {BaseUrl}",
                        inbox.Messages.Count,
                        connection.Entity.BaseUrl);
                    var madeProgress = await ProcessIncomingBotMessagesAsync(
                            connection, user, inbox.Messages, cancellationToken)
                        .ConfigureAwait(false);
                    if (!madeProgress)
                    {
                        _logger.LogDebug(
                            "Bot inbox on {BaseUrl} still has {Count} stored message(s); backing off {DelaySeconds:0}s",
                            connection.Entity.BaseUrl,
                            inbox.Messages.Count,
                            heldBackoff.TotalSeconds);
                        await Task.Delay(heldBackoff, cancellationToken).ConfigureAwait(false);
                        var doubled = TimeSpan.FromTicks(heldBackoff.Ticks * 2);
                        heldBackoff = doubled > TimeSpan.FromSeconds(5)
                            ? TimeSpan.FromSeconds(5)
                            : doubled;
                        continue;
                    }
                }

                heldBackoff = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Bot inbox processing failed on {BaseUrl}", connection.Entity.BaseUrl);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <returns>True when a message was stored for the first time or a receipt was accepted.</returns>
    private async Task<bool> ProcessIncomingBotMessagesAsync(
        MessengerServerConnection connection,
        UserEntity user,
        IReadOnlyList<BotClientInboundMessageDto> messages,
        CancellationToken cancellationToken)
    {
        if (!_manager.AllowsTraffic(connection) || messages.Count == 0)
            return false;

        var madeProgress = false;
        var privateKey = _auth.GetCurrentPrivateKey();
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var messageId = message.MessageId?.Trim() ?? "";
            var botId = message.BotNetworkId?.Trim() ?? "";
            if (messageId.Length == 0 || botId.Length == 0)
            {
                _logger.LogDebug(
                    "Skipping bot inbox item with empty MessageId/BotNetworkId on {BaseUrl}",
                    connection.Entity.BaseUrl);
                continue;
            }

            _logger.LogDebug(
                "Receive bot message {MessageId} from bot={BotId} on {BaseUrl}",
                messageId, botId, connection.Entity.BaseUrl);

            byte[] wire;
            try
            {
                wire = MessengerServerPayloadCodec.DecryptFromBase64(message.Message, privateKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decrypt bot message {MessageId} from {BotId}", messageId, botId);
                continue;
            }

            var deferReceipt = ChatWireCodec.TryParse(wire, out var parsedWire) &&
                               parsedWire is ChatWireTransferOffer { TotalChunks: > 0 };

            await _ingestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var chat = await _chats.FindChatByPeerNetworkIdAsync(user.Id, botId).ConfigureAwait(false);
                if (chat == null || !chat.IsBot)
                {
                    _logger.LogDebug(
                        "Skipping bot message {MessageId}: no local bot chat for {BotId}",
                        messageId,
                        botId);
                    continue;
                }

                var blocked = await _chats.IsPeerBlockedAsync(user.Id, botId, cancellationToken)
                    .ConfigureAwait(false);
                var isNew = await _chats.TryClaimServerMessageAsync(messageId).ConfigureAwait(false);
                if (isNew)
                {
                    await PersistIncomingServerWireAsync(
                            chat, wire, blocked, connection.Entity.BaseUrl, cancellationToken)
                        .ConfigureAwait(false);
                    madeProgress = true;
                    _logger.LogDebug(
                        "Persisted bot message {MessageId} chat={ChatId} wireLen={WireLen} blocked={Blocked}",
                        messageId, chat.Id, wire.Length, blocked);
                }
                else if (!deferReceipt)
                {
                    _logger.LogDebug(
                        "Duplicate bot message {MessageId} — skip ingest, still ack",
                        messageId);
                }
            }
            finally
            {
                _ingestGate.Release();
            }

            // Ack on every server that delivered a copy, even if we already ingested it once.
            // Chunked attachments stay on the server until the file itself has been downloaded:
            // a receipt deletes the ciphertext chunks.
            if (deferReceipt)
                continue;

            try
            {
                await TrackAsync(connection, () => connection.Api.SubmitBotMessageReceiptAsync(
                    new BotMessageReceiptRequest
                    {
                        MessageId = messageId,
                        ReceivedAtUtc = DateTime.UtcNow
                    },
                    cancellationToken)).ConfigureAwait(false);
                madeProgress = true;
                _logger.LogDebug("Bot message receipt OK for {MessageId}", messageId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Bot message receipt failed for {MessageId}", messageId);
            }
        }

        return madeProgress;
    }

    private async Task LongPollServerLoopAsync(
        MessengerServerConnection connection,
        UserEntity user,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_manager.AllowsTraffic(connection))
                break;

            try
            {
                var sw = Stopwatch.StartNew();
                // LongPollApi uses a separate HttpClient/socket pool from Api (send/receipts).
                var inbox = await TrackAsync(
                        connection,
                        () => connection.LongPollApi.PollEventsAsync(LongPollTimeoutSeconds, cancellationToken))
                    .ConfigureAwait(false);
                sw.Stop();

                if (!_manager.AllowsTraffic(connection))
                    break;

                if (inbox.Messages.Count > 0 ||
                    inbox.ChatRequests.Count > 0 ||
                    (inbox.Forwards?.Count ?? 0) > 0 ||
                    sw.Elapsed < TimeSpan.FromSeconds(LongPollTimeoutSeconds - 2))
                {
                    _manager.RecordProbeSuccess(connection.Entity.Id, sw.Elapsed);
                }

                if (inbox.ChatRequests.Count > 0)
                    await ProcessChatRequestsAsync(connection, user, inbox.ChatRequests, cancellationToken)
                        .ConfigureAwait(false);

                if (inbox.Messages.Count > 0)
                    await ProcessIncomingMessagesAsync(connection, user, inbox.Messages, cancellationToken)
                        .ConfigureAwait(false);

                var forwards = inbox.Forwards ?? [];
                if (forwards.Count > 0)
                    await ProcessIncomingForwardsAsync(connection, user, forwards, cancellationToken)
                        .ConfigureAwait(false);

                await ProcessDeliveryReceiptsAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_manager.AllowsTraffic(connection))
                    break;

                _logger.LogDebug(ex, "Long-poll failed on {BaseUrl}", connection.Entity.BaseUrl);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ProcessIncomingForwardsAsync(
        MessengerServerConnection connection,
        UserEntity user,
        IReadOnlyList<ForwardDto> forwards,
        CancellationToken cancellationToken)
    {
        if (!_manager.AllowsTraffic(connection) || forwards.Count == 0)
            return;

        var self = user.NetworkIdShort.Trim();
        foreach (var forward in forwards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!string.Equals(forward.TgtNetworkId?.Trim(), self, StringComparison.Ordinal))
                    continue;

                byte[] payload;
                try
                {
                    payload = Convert.FromBase64String(forward.PayloadBase64 ?? "");
                }
                catch (FormatException ex)
                {
                    _logger.LogWarning(ex, "Forward {ForwardId}: invalid payloadBase64 (best-effort)", forward.ForwardId);
                    continue;
                }

                _logger.LogDebug(
                    "Forward received on {BaseUrl} forwardId={ForwardId} src={Src} tgt={Tgt} kind={Kind} payloadBase64={Payload}",
                    connection.Entity.BaseUrl,
                    forward.ForwardId,
                    forward.SrcNetworkId,
                    forward.TgtNetworkId,
                    forward.Kind,
                    forward.PayloadBase64);

                switch (forward.Kind)
                {
                    case ForwardKind.PeerProfileRequest:
                        await ReplyToPeerProfileForwardAsync(connection, user, forward, payload, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case ForwardKind.PeerProfileReply:
                        await HandlePeerProfileForwardReplyAsync(forward, payload, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    default:
                        _logger.LogWarning("Forward {ForwardId}: unsupported kind {Kind}", forward.ForwardId,
                            forward.Kind);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Forward {ForwardId} handling failed (best-effort); continuing",
                    forward.ForwardId);
            }
        }
    }

    private async Task ReplyToPeerProfileForwardAsync(
        MessengerServerConnection connection,
        UserEntity user,
        ForwardDto request,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (!PeerProfileWireCodec.TryParseRequest(payload, out var nonce, out var sender))
        {
            _logger.LogWarning("Forward {ForwardId}: PeerProfileRequest parse failed", request.ForwardId);
            return;
        }

        var senderId = sender.ToShortString();
        if (!string.Equals(senderId, request.SrcNetworkId?.Trim(), StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Forward {ForwardId}: srcNetworkId mismatch with 0x44 sender",
                request.ForwardId);
            return;
        }

        string aboutMe = "";
        byte[]? avatar = null;
        if (_localProfile != null)
        {
            try
            {
                (aboutMe, avatar) = await _localProfile.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Local profile source failed while answering forward request");
            }
        }

        var localId = CompressedNetworkId.FromShortString(user.NetworkIdShort.Trim());
        var replyBytes = PeerProfileWireCodec.BuildReply(nonce, localId, aboutMe, avatar);
        var dto = new ForwardRequest
        {
            ForwardId = Guid.NewGuid().ToString("N"),
            SrcNetworkId = user.NetworkIdShort.Trim(),
            TgtNetworkId = senderId,
            Kind = ForwardKind.PeerProfileReply,
            PayloadBase64 = Convert.ToBase64String(replyBytes)
        };

        try
        {
            await TrackAsync(connection, () => connection.Api.ForwardAsync(dto, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Prefer the server that delivered the request; fall back to other trusted servers.
            _logger.LogDebug(ex, "Forward reply on {BaseUrl} failed; trying other servers", connection.Entity.BaseUrl);
            await TryPostForwardAsync(dto, senderId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandlePeerProfileForwardReplyAsync(
        ForwardDto forward,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (!PeerProfileWireCodec.TryParseReply(payload, out var nonce, out var responderId, out var aboutMe,
                out var avatar))
        {
            _logger.LogWarning("Forward {ForwardId}: PeerProfileReply parse failed", forward.ForwardId);
            return;
        }

        if (!_pendingForwardProfileNonces.TryRemove(nonce, out var expectedId) ||
            !string.Equals(expectedId, responderId.ToShortString(), StringComparison.Ordinal))
        {
            return;
        }

        if (_peerProfiles != null)
        {
            try
            {
                await _peerProfiles.UpsertAsync(responderId, null, aboutMe, avatar, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Peer profile store write failed for {NetworkId} (best-effort)",
                    responderId.ToShortString());
            }
        }

        try
        {
            PeerAboutMeApplied?.Invoke(responderId, aboutMe ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PeerAboutMeApplied callback failed");
        }
    }

    private async Task<bool> TryPostForwardAsync(
        ForwardRequest dto,
        string peerIdHint,
        CancellationToken cancellationToken)
    {
        var targets = await CollectPeerServerTargetsAsync(dto.TgtNetworkId, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            _logger.LogDebug("No messenger server for forward to {PeerId}", peerIdHint);
            return false;
        }

        foreach (var conn in targets)
        {
            if (!_manager.AllowsTraffic(conn))
                continue;
            try
            {
                await TrackAsync(conn, () => conn.Api.ForwardAsync(dto, cancellationToken)).ConfigureAwait(false);
                return true;
            }
            catch (MessengerServerApiException ex) when (
                string.Equals(ex.ErrorCode, "PeerOffline", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Forward PeerOffline on {BaseUrl} for {PeerId}", conn.Entity.BaseUrl, peerIdHint);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Forward failed on {BaseUrl} for {PeerId}", conn.Entity.BaseUrl, peerIdHint);
            }
        }

        return false;
    }

    private async Task ProcessChatRequestsAsync(
        MessengerServerConnection connection,
        UserEntity user,
        IReadOnlyList<ChatRequestDto> requests,
        CancellationToken cancellationToken)
    {
        if (!_manager.AllowsTraffic(connection))
            return;

        if (requests.Count == 0)
            return;

        var ourPublic = RsaKeySerializer.SerializePublic(_auth.GetCurrentPublicKey());
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var peerId = request.NetworkId.Trim();
            if (string.IsNullOrEmpty(peerId) ||
                ChatRepository.PeerNetworkIdsEqual(peerId, user.NetworkIdShort))
                continue;

            var blocked = await _chats.IsPeerBlockedAsync(user.Id, peerId, cancellationToken).ConfigureAwait(false);
            var existing = await _chats.FindChatByPeerNetworkIdAsync(user.Id, peerId).ConfigureAwait(false);
            var requestKey = request.PublicKey.Trim();
            var isNewChat = existing == null;
            var keyChanged = existing != null &&
                             !SafetyNumber.PublicKeyJsonEquals(existing.PeerRsaPublicJson, requestKey);
            var peerNick = await ResolvePeerNicknameAsync(connection, peerId, cancellationToken).ConfigureAwait(false);
            var source = PeerKeySource.Server(connection.Entity.BaseUrl);
            LogChatRequest("receive", connection, peerId);
            ChatEntity chat;
            if (isNewChat || keyChanged)
            {
                chat = await _chats.AddChatAsync(
                    user.Id,
                    peerNick,
                    peerId,
                    request.PublicKey,
                    peerId,
                    user.DataUdpPort,
                    remote: true,
                    keySource: source).ConfigureAwait(false);
            }
            else
            {
                chat = existing!;
                await _chats.TryUpdatePeerNicknameAsync(chat.Id, peerNick).ConfigureAwait(false);
            }

            if (blocked)
            {
                _logger.LogInformation(
                    "Ignored UI for chat request from blocked peer {PeerId} (chat {ChatId})",
                    peerId,
                    chat.Id);
                continue;
            }

            // Reply only when the peer may still need our key. Always-reply + failover republish
            // used to ping-pong ChatRequests and re-fire IncomingChatInvite on every long-poll.
            if (isNewChat || keyChanged)
            {
                try
                {
                    LogChatRequest("reply", connection, peerId);
                    await TrackAsync(connection, () => connection.Api.CreateChatRequestAsync(
                        new ChatRequestCreateRequest
                        {
                            PublicKey = ourPublic,
                            TargetNetworkId = peerId
                        },
                        cancellationToken)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var (host, port) = SplitServerHostPort(connection.Entity.BaseUrl);
                    _logger.LogWarning(ex, "Reply ChatRequest failed for peer {PeerId} on {Host}:{Port}",
                        peerId, host, port);
                }
            }

            if (isNewChat)
            {
                _logger.LogInformation(
                    "Accepted server chat request from {PeerId} into local chat {ChatId}",
                    peerId,
                    chat.Id);
                // Open UI only for a genuine new chat — not duplicate/failover re-delivery.
                await _chats.NotifyIncomingChatInviteAsync(chat.Id, cancellationToken).ConfigureAwait(false);
            }
            else if (_sessions.IsStarted(chat.Id) && _sessions.TryGetSession(chat.Id, out var session))
            {
                _logger.LogInformation(
                    "Server ChatRequest from {PeerId}: resyncing started session for chat {ChatId}",
                    peerId,
                    chat.Id);
                await session.HandlePeerInviteResyncAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> ResolvePeerNicknameAsync(
        MessengerServerConnection connection,
        string peerNetworkId,
        CancellationToken cancellationToken)
    {
        try
        {
            var list = await TrackAsync(connection, () => connection.Api.GetClientsAsync(cancellationToken))
                .ConfigureAwait(false);
            _manager.ReplaceRegisteredClients(connection.Entity.Id, list.Select(c => c.NetworkId));
            var hit = list.FirstOrDefault(c =>
                string.Equals(c.NetworkId.Trim(), peerNetworkId, StringComparison.Ordinal));
            var nick = hit?.Nick?.Trim() ?? "";
            if (nick.Length > 0 &&
                !string.Equals(nick, peerNetworkId, StringComparison.OrdinalIgnoreCase))
                return nick;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "GetClients for nickname of {PeerId} failed", peerNetworkId);
        }

        return peerNetworkId;
    }

    private async Task ProcessIncomingMessagesAsync(
        MessengerServerConnection connection,
        UserEntity user,
        IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken)
    {
        if (!_manager.AllowsTraffic(connection))
            return;

        if (messages.Count == 0)
            return;

        _logger.LogDebug(
            "ProcessIncoming: {Count} message(s) on {BaseUrl}",
            messages.Count,
            connection.Entity.BaseUrl);

        var privateKey = _auth.GetCurrentPrivateKey();
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ChatRepository.PeerNetworkIdsEqual(message.TgtNetworkId, user.NetworkIdShort))
                continue;

            _logger.LogDebug(
                "Receive server message {MessageId} from={Src} to={Tgt} on {BaseUrl}",
                message.MessageId,
                message.SrcNetworkId,
                message.TgtNetworkId,
                connection.Entity.BaseUrl);

            if (string.IsNullOrWhiteSpace(message.EncryptedDataBase64))
            {
                _logger.LogDebug("Skipping server message {MessageId}: empty payload", message.MessageId);
                continue;
            }

            var peerId = message.SrcNetworkId.Trim();
            ServerPayloadOpenResult opened;
            try
            {
                var blob = Convert.FromBase64String(message.EncryptedDataBase64.Trim());
                opened = MessengerServerPayloadCodec.Open(blob, privateKey, peerId, _serverSessions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decrypt server message {MessageId}", message.MessageId);
                continue;
            }

            if (opened.ResponseEnvelope is { Length: > 0 } response)
            {
                try
                {
                    await TryDeliverRawEnvelopeAsync(peerId, user, response, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex,
                        "Session control reply failed for message {MessageId}", message.MessageId);
                }
            }

            var wire = opened.ChatWire;
            if (wire != null)
            {
            await _ingestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var chat = await _chats.FindChatByPeerNetworkIdAsync(user.Id, peerId).ConfigureAwait(false);
                if (chat == null)
                {
                    _logger.LogDebug(
                        "Skipping server message {MessageId}: no local chat for peer {PeerId}",
                        message.MessageId,
                        peerId);
                    continue;
                }

                var blocked = await _chats.IsPeerBlockedAsync(user.Id, peerId, cancellationToken)
                    .ConfigureAwait(false);

                var isNew = await _chats.TryClaimServerMessageAsync(message.MessageId).ConfigureAwait(false);
                if (!isNew)
                {
                    _logger.LogDebug(
                        "Duplicate server message {MessageId} from {BaseUrl} — skip ingest, still ack",
                        message.MessageId,
                        connection.Entity.BaseUrl);
                }
                else
                {
                    await PersistIncomingServerWireAsync(
                            chat, wire, blocked, connection.Entity.BaseUrl, cancellationToken)
                        .ConfigureAwait(false);
                    _logger.LogDebug(
                        "Persisted server message {MessageId} chat={ChatId} IsBot={IsBot} wireLen={WireLen}",
                        message.MessageId, chat.Id, chat.IsBot, wire.Length);
                }
            }
            finally
            {
                _ingestGate.Release();
            }
            }

            // Ack on every server that delivered a copy, even if we already displayed it once.
            try
            {
                await TrackAsync(connection, () => connection.Api.SubmitDeliveryReceiptAsync(
                    new DeliveryReceiptRequest
                    {
                        MessageId = message.MessageId,
                        ReceivedAtUtc = DateTime.UtcNow
                    },
                    cancellationToken)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Delivery receipt failed for {MessageId}", message.MessageId);
            }
        }
    }

    /// <summary>
    /// Pulls delivery tickets for messages we sent (destructive on the server) and marks
    /// matching local outgoing rows as <see cref="MessageDeliveryStatus.Delivered"/>.
    /// </summary>
    private async Task ProcessDeliveryReceiptsAsync(
        MessengerServerConnection connection,
        CancellationToken cancellationToken)
    {
        if (!_manager.AllowsTraffic(connection))
            return;

        IReadOnlyList<DeliveryReceiptDto> receipts;
        try
        {
            receipts = await TrackAsync(
                    connection,
                    () => connection.Api.GetDeliveryReceiptsAsync(cancellationToken))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "GetDeliveryReceipts failed on {BaseUrl}", connection.Entity.BaseUrl);
            return;
        }

        if (receipts.Count == 0)
            return;

        var touchedChatIds = new HashSet<int>();
        foreach (var receipt in receipts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mapped = await _chats.TryTakeOutgoingServerMessageAsync(receipt.MessageId).ConfigureAwait(false);
            if (mapped == null)
                continue;

            var (localMessageId, chatId) = mapped.Value;
            await _chats
                .UpdateMessageDeliveryStatusAsync(localMessageId, MessageDeliveryStatus.Delivered)
                .ConfigureAwait(false);
            touchedChatIds.Add(chatId);
            _logger.LogDebug(
                "Delivery receipt applied: serverMessageId={ServerMessageId} localMessageId={LocalMessageId}",
                receipt.MessageId,
                localMessageId);
        }

        foreach (var chatId in touchedChatIds)
        {
            if (_sessions.TryGetSession(chatId, out var session) && session != null)
                session.NotifyMessagesChangedFromExternal();
        }
    }

    /// <summary>
    /// Persist a server wire without waiting for P2P handshake. Session ingest is used only
    /// when the session is already ready; otherwise (or on failure) write straight to SQLite
    /// so the UI event fires immediately.
    /// </summary>
    private async Task PersistIncomingServerWireAsync(
        ChatEntity chat,
        byte[] wire,
        bool blocked,
        string? blobServerBaseUrl,
        CancellationToken cancellationToken)
    {
        if (_sessions.TryGetSession(chat.Id, out var session) &&
            session != null &&
            !session.AcceptsServerTransport)
        {
            _logger.LogInformation(
                "Chat {ChatId}: server inbox skipped (delivery path is mesh)", chat.Id);
            return;
        }

        if (!blocked &&
            session != null &&
            session.IsReadyForServerReceive)
        {
            try
            {
                await session.IngestIncomingWireFromServerAsync(wire, cancellationToken, blobServerBaseUrl)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session ingest failed for chat {ChatId}, using repository", chat.Id);
            }
        }

        _logger.LogDebug(
            "Ingest wire via repository chat={ChatId} IsBot={IsBot} wireLen={WireLen}",
            chat.Id, chat.IsBot, wire.Length);
        await IngestWireIntoRepositoryAsync(chat.Id, wire, blobServerBaseUrl).ConfigureAwait(false);
    }

    /// <summary>
    /// Download GET. A 404 means the blob is absent or the route is missing, not that the server is down.
    /// </summary>
    private async Task<T> CallDownloadAsync<T>(
        MessengerServerConnection connection,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        if (!_manager.AllowsTraffic(connection))
            throw new InvalidOperationException("Messenger server is not trusted.");

        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            _manager.RecordRequestSuccess(connection.Entity.Id);
            return result;
        }
        catch (MessengerServerApiException ex) when ((int)ex.StatusCode == 404)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _manager.RecordRequestFailure(connection.Entity.Id);
            throw;
        }
    }

    private bool MarkBotApiUnavailable(string? baseUrl)
    {
        var key = SqliteMessengerServerRepository.NormalizeBaseUrl(baseUrl ?? "");
        if (key.Length == 0)
            return false;
        return _serversWithoutBotApi.TryAdd(key, 0);
    }

    private void MarkBotApiAvailable(string? baseUrl)
    {
        var key = SqliteMessengerServerRepository.NormalizeBaseUrl(baseUrl ?? "");
        if (key.Length == 0)
            return;
        _serversWithoutBotApi.TryRemove(key, out _);
    }

    private bool SupportsBotApi(MessengerServerConnection connection) =>
        !_serversWithoutBotApi.ContainsKey(
            SqliteMessengerServerRepository.NormalizeBaseUrl(connection.Entity.BaseUrl));

    private async Task TrackAsync(MessengerServerConnection connection, Func<Task> action)
    {
        if (!_manager.AllowsTraffic(connection))
            throw new InvalidOperationException("Messenger server is not trusted.");

        try
        {
            await action().ConfigureAwait(false);
            _manager.RecordRequestSuccess(connection.Entity.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _manager.RecordRequestFailure(connection.Entity.Id);
            throw;
        }
    }

    private async Task<T> TrackAsync<T>(MessengerServerConnection connection, Func<Task<T>> action)
    {
        if (!_manager.AllowsTraffic(connection))
            throw new InvalidOperationException("Messenger server is not trusted.");

        try
        {
            var result = await action().ConfigureAwait(false);
            _manager.RecordRequestSuccess(connection.Entity.Id);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _manager.RecordRequestFailure(connection.Entity.Id);
            throw;
        }
    }

    private async Task IngestWireIntoRepositoryAsync(int chatId, byte[] wire, string? blobServerBaseUrl = null)
    {
        if (ChatWireCodec.TryParse(wire, out var parsed) && parsed != null)
        {
            switch (parsed)
            {
                case ChatWireText t:
                    await _chats.AddMessageAsync(chatId, false, t.Text).ConfigureAwait(false);
                    return;
                case ChatWireImage img:
                    await _chats.AddImageMessageAsync(chatId, false, img.MimeType, img.ImageBytes)
                        .ConfigureAwait(false);
                    return;
                case ChatWireFile f:
                    await _chats.AddFileMessageAsync(chatId, false, f.FileName, f.MimeType, f.FileBytes)
                        .ConfigureAwait(false);
                    return;
                case ChatWireTransferOffer offer:
                    var text = string.IsNullOrWhiteSpace(offer.FileName) ? "[Входящее вложение]" : offer.FileName;
                    var host = !string.IsNullOrWhiteSpace(offer.Host)
                        ? offer.Host
                        : blobServerBaseUrl?.Trim() ?? "";
                    await _chats.AddIncomingTransferOfferAsync(
                            chatId,
                            text,
                            offer.MimeType,
                            offer.TransferId,
                            offer.TransferToken,
                            offer.PayloadKind,
                            offer.FileName,
                            offer.SizeBytes,
                            host,
                            offer.Port,
                            offer.ExpiresUtcTicks,
                            offer.TotalChunks)
                        .ConfigureAwait(false);
                    return;
                case ChatWireUserInfo userInfo:
                    await ApplyIncomingUserInfoFromServerAsync(chatId, userInfo, CancellationToken.None)
                        .ConfigureAwait(false);
                    return;
                default:
                    await _chats.AddMessageAsync(chatId, false, "[Серверное сообщение: неподдерживаемый тип]")
                        .ConfigureAwait(false);
                    return;
            }
        }

        var fallback = Encoding.UTF8.GetString(wire);
        await _chats.AddMessageAsync(chatId, false, fallback).ConfigureAwait(false);
    }

    private async Task ApplyIncomingUserInfoFromServerAsync(
        int chatId,
        ChatWireUserInfo info,
        CancellationToken cancellationToken)
    {
        if (_peerProfiles == null)
            return;

        try
        {
            var chat = await _chats.GetChatAsync(chatId).ConfigureAwait(false);
            if (chat == null || string.IsNullOrWhiteSpace(chat.PeerNetworkIdShort))
                return;

            var peerId = CompressedNetworkId.FromShortString(chat.PeerNetworkIdShort);
            var nick = string.IsNullOrWhiteSpace(chat.PeerNickname) ? null : chat.PeerNickname.Trim();
            await _peerProfiles.UpsertAsync(peerId, nick, info.AboutMe ?? "", info.Avatar, cancellationToken)
                .ConfigureAwait(false);
            PeerAboutMeApplied?.Invoke(peerId, info.AboutMe ?? "");
            _logger.LogInformation(
                "Chat {ChatId}: applied peer UserInfo from server inbox (aboutLen={AboutLen}, avatarBytes={AvatarBytes})",
                chatId, info.AboutMe?.Length ?? 0, info.Avatar?.Length ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: failed to apply UserInfo from server (best-effort)", chatId);
        }
    }

    private void OnFailoverCompleted(object? sender, MessengerServerFailoverEventArgs e)
    {
        if (e.SwitchedToMesh || e.FallbackServer == null)
            return;
        var token = _cts?.Token ?? CancellationToken.None;
        _ = RepublishAllChatRequestsAsync(token);
    }

    private async Task RepublishAllChatRequestsAsync(CancellationToken cancellationToken)
    {
        var user = _auth.CurrentUser;
        if (user == null)
            return;
        try
        {
            var chats = await _chats.ListChatsAsync(user.Id).ConfigureAwait(false);
            foreach (var chat in chats)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PublishChatRequestAsync(chat.PeerNetworkIdShort, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Republished ChatRequest for {Count} chats after messenger-server failover",
                chats.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // expected
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to republish ChatRequest after messenger-server failover");
        }
    }

    private void LogChatRequest(string action, MessengerServerConnection connection, string peerNetworkId)
    {
        var (host, port) = SplitServerHostPort(connection.Entity.BaseUrl);
        _logger.LogInformation(
            "ChatRequest {Action} server {Host}:{Port} ({BaseUrl}) peer={PeerNetworkId}",
            action, host, port, connection.Entity.BaseUrl, peerNetworkId);
    }

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    internal static (string Host, int Port) SplitServerHostPort(string? baseUrl)
    {
        if (!Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out var uri))
            return ((baseUrl ?? "").Trim(), 0);
        var port = uri.IsDefaultPort
            ? uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80
            : uri.Port;
        return (uri.Host, port);
    }

    private static async Task WaitIgnoreCancelAsync(Task? task)
    {
        if (task == null)
            return;
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _ingestGate.Dispose();
    }
}
