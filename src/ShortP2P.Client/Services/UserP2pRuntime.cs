using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShortP2P.Auth;
using ShortP2P.Auth.Data;
using ShortP2P.Client.Bluetooth;
using ShortP2P.Client.ChatMedia;
using ShortP2P.Client.Data;
using ShortP2P.Client.Qr;
using ShortP2P.Client.Routing;
using ShortP2P.Client.Services.MessengerServers;
using ShortP2P.Client.Transceivers;
using ShortP2P.Client.Transport;
using ShortP2P.Discovery;
using ShortP2P.Discovery.Ble;
using ShortP2P.Discovery.Pings;
using ShortP2P.Discovery.Profile;
using ShortP2P.Discovery.RouteTables;
using ShortP2P.Discovery.Transceivers;
using ShortP2P.MessengerServer.Contracts.Dtos;
using ShortP2P.Transport;
using ShortP2P.Transport.Abstractions;

namespace ShortP2P.Client.Services;

/// <summary>Настройки маршрутизации и LAN discovery для сессии пользователя.</summary>
public sealed class UserP2pRuntime : IAsyncDisposable
{
    private readonly AuthService _auth;
    private readonly IBleDiscoveredPeerStore? _bleDiscoveredPeerStore;
    private readonly IAdditionalTransportProvider? _additionalTransports;
    private readonly IBluetoothTransportProvider? _bluetooth;
    private readonly ChatMediaOptions _chatMedia;
    private readonly ChatRepository _chats;
    private readonly P2pCryptoSessionCache _cryptoSessionCache;

    private readonly SemaphoreSlim _dataPortGate = new(1, 1);
    private readonly List<Task> _dataReceiveTasks = [];
    private readonly HashSet<ITransport> _attachedAdditionalDataTransports = [];

    /// <summary>Сериализация старта сессии по discovery-пингу для одного чата (без await внутри lock).</summary>
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _discoverySessionStartGates = new();

    private readonly SemaphoreSlim _inviteListenerGate = new(1, 1);
    private readonly HashSet<string> _selfUdpAddresses = new(StringComparer.OrdinalIgnoreCase);

    private readonly ChatSessionCache _sessionCache;
    private readonly ILogger<UserP2pRuntime> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly P2pRoutingSettingsStore _store;
    private UserEntity? _currentDataPortUser;
    private CancellationTokenSource? _dataCts;
    private DataPortMultiplexer? _dataPortMultiplexer;

    private volatile bool _discoveryHooked;
    private CancellationTokenSource? _inviteCts;
    private readonly List<Task> _inviteReceiveTasks = [];
    private UdpTransport? _inviteUdp;
    private CancellationTokenSource? _presencePingWorkCts;

    public UserP2pRuntime(P2pRoutingSettingsStore store, AuthService auth, ChatRepository chats,
        ChatMediaOptions chatMedia, IUdpTransportFactory udpTransportFactory, ChatSessionCache sessionCache,
        P2pCryptoSessionCache cryptoSessionCache,
        IBluetoothTransportProvider? bluetooth = null,
        IEnumerable<ITransport>? additionalDiscoveryTransports = null,
        IRouteTableSnapshotSource? routeTableSnapshotSource = null,
        IDiscoveryPingStore? discoveryPingStore = null,
        IBleShortP2PPeripheralScanner? blePeripheralScanner = null,
        IBleDiscoveredPeerStore? bleDiscoveredPeerStore = null,
        IBluetoothPresencePingTargetsProvider? bluetoothPresencePingTargetsProvider = null,
        ILoggerFactory? loggerFactory = null,
        MessengerServerSyncService? messengerServers = null,
        IPeerProfileStore? peerProfileStore = null,
        ILocalPeerProfileSource? localPeerProfileSource = null,
        IAdditionalTransportProvider? additionalTransports = null)
    {
        _store = store;
        _auth = auth;
        _bleDiscoveredPeerStore = bleDiscoveredPeerStore;
        _chats = chats;
        _chatMedia = chatMedia;
        UdpTransportFactory = udpTransportFactory;
        _sessionCache = sessionCache;
        _cryptoSessionCache = cryptoSessionCache;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<UserP2pRuntime>();
        _bluetooth = bluetooth;
        _additionalTransports = additionalTransports;
        MessengerServers = messengerServers;
        PeerProfiles = peerProfileStore;
        LocalScan = new LocalNetworkScanner(Settings, udpTransportFactory, () => _bluetooth?.Current,
            additionalDiscoveryTransports, routeTableSnapshotSource, discoveryPingStore, blePeripheralScanner,
            bleDiscoveredPeerStore, bluetoothPresencePingTargetsProvider, peerProfileStore, localPeerProfileSource,
            _loggerFactory.CreateLogger("ShortP2P.Discovery.LocalNetworkScanner"));
        
        if (MessengerServers == null) return;
        
        LocalScan.PrioritizedExternalDiscoveryRound = async ct =>
        {
            var remote = await MessengerServers.KeepAliveAndListRemoteClientsAsync(ct).ConfigureAwait(false);
            var entries = remote.Select(ToDirectoryEntry).ToArray();
            LocalScan.ApplyMessengerServerDirectory(entries);
            await SyncChatNicknamesFromPresenceAsync(remote, ct).ConfigureAwait(false);
            try
            {
                var bots = await MessengerServers.ProbeAndListRemoteBotsAsync(ct).ConfigureAwait(false);
                LocalScan.ApplyBotDirectory(MessengerServerSyncService.ToBotDirectoryEntries(bots));
                await MessengerServers.SyncBotKeysFromCatalogAsync(bots, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GetBots discovery round failed");
            }
        };
        LocalScan.RequestPeerProfileViaMessengerServer = (id, ct) =>
            MessengerServers.RequestPeerProfileViaForwardAsync(id, ct);
        MessengerServers.PeerAboutMeApplied = (id, about) => LocalScan.ApplyCachedAboutMe(id, about);
    }

    /// <summary>Optional HTTPS messenger-server sync (long-poll inbox, ChatRequest, messages).</summary>
    public MessengerServerSyncService? MessengerServers { get; }

    /// <summary>Локальный кэш профилей пиров (Avatar / AboutMe), без синхронизации с сервером.</summary>
    public IPeerProfileStore? PeerProfiles { get; }

    private async Task SyncChatNicknamesFromPresenceAsync(
        IReadOnlyList<ClientPresenceDto> remote,
        CancellationToken cancellationToken)
    {
        var user = _auth.CurrentUser;
        if (user == null || remote.Count == 0)
            return;

        foreach (var client in remote)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = client.NetworkId.Trim();
            var nick = client.Nick?.Trim() ?? "";
            if (id.Length == 0 || nick.Length == 0)
                continue;
            if (string.Equals(nick, id, StringComparison.OrdinalIgnoreCase))
                continue;

            var chat = await _chats.FindChatByPeerNetworkIdAsync(user.Id, id).ConfigureAwait(false);
            if (chat == null)
                continue;
            if (await _chats.TryUpdatePeerNicknameAsync(chat.Id, nick).ConfigureAwait(false))
                await RefreshSessionChatRowAsync(chat.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private static MessengerServerDirectoryEntry ToDirectoryEntry(ClientPresenceDto client)
    {
        var lastSeen = DateTime.SpecifyKind(client.LastSeenAtUtc, DateTimeKind.Utc);
        return new MessengerServerDirectoryEntry(
            client.NetworkId.Trim(),
            client.Nick.Trim(),
            client.IsOnline,
            new DateTimeOffset(lastSeen));
    }

    /// <summary>Глобальный invite-транспивер (порт 17502). Поднимается в <see cref="EnsureInviteListenerRunningAsync" />.</summary>
    public InviteTransceiver? Invite { get; private set; }

    /// <summary>Handshake-транспивер на data UDP-порту пользователя.</summary>
    public HandshakeTransceiver? Handshake => _dataPortMultiplexer?.Handshake;

    /// <summary>Cipher (рядовые сообщения) транспивер на data UDP-порту пользователя.</summary>
    public MessageTransceiver? Message => _dataPortMultiplexer?.Message;

    /// <summary>BLE NetworkId транспивер на data-порту (префикс 0x33,0x55).</summary>
    public BleNetworkIdTransceiver? BleNetworkId => _dataPortMultiplexer?.BleNetworkId;

    /// <summary>UDP-транспорт data-порта (для отправки invite-replies/control в адрес пира).</summary>
    public UdpTransport? DataUdp { get; private set; }

    /// <summary>
    ///     True when UDP transport is enabled and both the invite listener and the data socket came up.
    ///     False after a bind/start failure — callers should use the BLE / messenger-server workaround.
    /// </summary>
    public bool IsUdpTransportEstablished =>
        Settings.EnableUdpTransport && DataUdp != null && Invite != null;

    public P2pRoutingSettings Settings { get; } = new();

    /// <summary>Shared compress/send queue for images, video and documents (see <see cref="BinarySendScheduler"/>).</summary>
    public BinarySendScheduler BinarySends { get; } = new();

    public ITransport? BluetoothTransport => _bluetooth?.Current;

    /// <summary>Optional host transports beyond UDP/Bluetooth (e.g. LoRa/Meshtastic).</summary>
    public IAdditionalTransportProvider? AdditionalTransports => _additionalTransports;

    /// <summary>
    ///     Сканирование LAN: presence UDP 17501; wire discovery (gossip / маршруты)
    ///     <see cref="UdpPeerDiscoveryOptions.DefaultDiscoveryUdpPort" />
    /// </summary>
    public LocalNetworkScanner LocalScan { get; }

    public IUdpTransportFactory UdpTransportFactory { get; }

    public LanChatStartContext CreateLanChatStartContext() =>
        new()
        {
            MessengerServers = MessengerServers,
            UdpTransportFactory = UdpTransportFactory,
            Settings = Settings,
            BluetoothTransport = BluetoothTransport,
            BluetoothAdapterMac = Settings.SelectedBluetoothAdapterMac,
            StopInviteListenerAsync = StopInviteListenerAsync,
            EnsureInviteListenerAsync = EnsureInviteListenerRunningAsync,
            ResolveOutboundTransport = destination => _additionalTransports?.ResolveOutbound(destination)
        };

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    public ChatP2PSession GetSession(ChatEntity chat, UserEntity user, AuthService auth, ChatRepository repo,
        SynchronizationContext? uiSync)
    {
        return _sessionCache.GetSession(chat.Id,
            () => ChatP2PSession.Create(chat, user, auth, repo, this, uiSync, Settings, LocalScan, _chatMedia,
                _cryptoSessionCache, _loggerFactory.CreateLogger<ChatP2PSession>()),
            s => s.ApplyChatRow(chat));
    }

    public bool IsChatSessionStarted(int chatId)
    {
        return _sessionCache.IsStarted(chatId);
    }

    public void MarkChatSessionStarted(int chatId)
    {
        _sessionCache.MarkStarted(chatId);
    }

    /// <summary>
    ///     TRL-7: push local AboutMe + Avatar to every chat contact (best-effort; mesh and/or messenger server).
    /// </summary>
    public async Task BroadcastLocalUserInfoToContactsAsync(CancellationToken cancellationToken = default)
    {
        var user = _auth.CurrentUser;
        if (user == null)
            return;

        IReadOnlyList<ChatEntity> chats;
        try
        {
            chats = await _chats.ListChatsAsync(user.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UserInfo broadcast: failed to list chats");
            return;
        }

        foreach (var chat in chats)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await _chats.IsPeerBlockedAsync(user.Id, chat.PeerNetworkIdShort, cancellationToken)
                        .ConfigureAwait(false))
                    continue;

                var session = GetSession(chat, user, _auth, _chats, null);
                await session.SendLocalUserInfoAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "UserInfo broadcast to chat {ChatId} failed (best-effort)", chat.Id);
            }
        }
    }

    /// <summary>Останавливает и снимает P2P-сессию для удалённого из БД чата.</summary>
    public async Task RemoveChatSessionAsync(int chatId, CancellationToken cancellationToken = default)
    {
        _sessionCache.TryRemove(chatId, out var session);

        if (_discoverySessionStartGates.TryRemove(chatId, out var gate))
            try
            {
                gate.Dispose();
            }
            catch
            {
                // ignore
            }

        if (session != null)
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
    }

    /// <summary>
    ///     Останавливает приёмник инвайтов на <see cref="ChatInviteCodec.InviteUdpPort" /> (перед временным bind
    ///     из <see cref="LanChatStartFromDiscovery" /> на том же порту).
    /// </summary>
    public async Task StopInviteListenerAsync(CancellationToken cancellationToken = default)
    {
        await _inviteListenerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopInviteListenerCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inviteListenerGate.Release();
        }
    }

    /// <summary>
    ///     Поднимает приёмник входящих <see cref="ChatInviteCodec" /> на <see cref="ChatInviteCodec.InviteUdpPort" />
    ///     (отдельно от data/чата на <see cref="UserEntity.DataUdpPort" />).
    /// </summary>
    public async Task EnsureInviteListenerRunningAsync(UserEntity user, CancellationToken cancellationToken = default)
    {
        await _inviteListenerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_inviteUdp != null)
                return;

            _inviteCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _inviteUdp = UdpTransportFactory.Acquire(IPAddress.Any, ChatInviteCodec.InviteUdpPort,
                true);
            await _inviteUdp.StartAsync(cancellationToken).ConfigureAwait(false);
            Invite = new InviteTransceiver(_inviteUdp);
            Invite.GotData += OnInviteReceived;
            await Invite.StartAsync(_inviteCts.Token).ConfigureAwait(false);
            _inviteReceiveTasks.AddRange(Task.Run(() => InviteReceiveLoopAsync(_inviteUdp, Invite, _inviteCts.Token), _inviteCts.Token),
                Task.Run(() => InviteReceiveLoopAsync(_inviteUdp, Invite, _inviteCts.Token), _inviteCts.Token));
        }
        catch
        {
            await StopInviteListenerCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _inviteListenerGate.Release();
        }
    }

    /// <summary>
    ///     Поднимает один UDP-сокет на <see cref="UserEntity.DataUdpPort" /> и поверх него
    ///     <see cref="DataPortMultiplexer" /> с handshake/cipher транспиверами. Вызывается один раз для
    ///     текущего пользователя; повторный вызов с тем же user — no-op.
    /// </summary>
    private async Task EnsureDataPortRunningAsync(UserEntity user, CancellationToken cancellationToken)
    {
        await _dataPortGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_dataPortMultiplexer != null && _currentDataPortUser?.Id == user.Id)
            {
                await AttachAdditionalDataTransportsAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            await StopDataPortCoreAsync(cancellationToken).ConfigureAwait(false);
            _dataCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var inbound = new List<ITransport>();
            if (Settings.EnableUdpTransport)
            {
                try
                {
                    var udp = UdpTransportFactory.Acquire(IPAddress.Any, user.DataUdpPort);
                    DataUdp = udp;
                    await DataUdp.StartAsync(cancellationToken).ConfigureAwait(false);
                    inbound.Add(DataUdp);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Error while establishing connection by UDP on data port {Port}", user.DataUdpPort);
                    if (DataUdp != null)
                    {
                        var failed = DataUdp;
                        DataUdp = null;
                        try
                        {
                            await UdpTransportFactory.ReleaseAsync(failed, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
            }

            var bluetooth = BluetoothTransport;
            if (bluetooth != null && Settings.EnableBluetoothTransport)
                try
                {
                    await bluetooth.StartAsync(cancellationToken).ConfigureAwait(false);
                    inbound.Add(bluetooth);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // bluetooth subsystem optional
                    _logger.LogDebug(ex, "BLE data transport did not start");
                }

            if (_additionalTransports != null)
                foreach (var extra in _additionalTransports.GetDataTransports())
                {
                    if (extra == null || inbound.Contains(extra))
                        continue;
                    try
                    {
                        await extra.StartAsync(cancellationToken).ConfigureAwait(false);
                        inbound.Add(extra);
                        _attachedAdditionalDataTransports.Add(extra);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "Additional data transport kind {Kind} did not start", extra.Kind);
                    }
                }

            if (inbound.Count == 0)
                throw new InvalidOperationException(
                    "Error while establishing connection by UDP, and BLE / additional transports are not available.");

            _dataPortMultiplexer = new DataPortMultiplexer(ResolveDataOutboundTransport);
            _dataPortMultiplexer.BleNetworkId.GotData += OnBleNetworkIdReceived;
            await _dataPortMultiplexer.StartAsync(cancellationToken).ConfigureAwait(false);
            var dataToken = _dataCts.Token;
            foreach (var transport in inbound)
                _dataReceiveTasks.Add(Task.Run(() =>
                        DataPortReceiveLoopAsync(transport, _dataPortMultiplexer, dataToken),
                    dataToken));
            _currentDataPortUser = user;
        }
        catch
        {
            await StopDataPortCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _dataPortGate.Release();
        }
    }

    private ITransport? ResolveDataOutboundTransport(TransportAddress destination)
    {
        return destination.Kind switch
        {
            TransportKind.Udp when Settings.EnableUdpTransport => DataUdp,
            TransportKind.Bluetooth when Settings.EnableBluetoothTransport => BluetoothTransport,
            _ => _additionalTransports?.ResolveOutbound(destination)
        };
    }

    /// <summary>
    ///     Hot-attach host transports (e.g. LoRa) that appeared after the data port was already up.
    /// </summary>
    private async Task AttachAdditionalDataTransportsAsync(CancellationToken cancellationToken)
    {
        if (_additionalTransports == null || _dataPortMultiplexer == null || _dataCts == null)
            return;

        var dataToken = _dataCts.Token;
        foreach (var extra in _additionalTransports.GetDataTransports())
        {
            if (extra == null || !_attachedAdditionalDataTransports.Add(extra))
                continue;
            try
            {
                await extra.StartAsync(cancellationToken).ConfigureAwait(false);
                var transport = extra;
                var mux = _dataPortMultiplexer;
                _dataReceiveTasks.Add(Task.Run(() => DataPortReceiveLoopAsync(transport, mux, dataToken),
                    dataToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _attachedAdditionalDataTransports.Remove(extra);
                _logger.LogDebug(ex, "Late attach of additional transport kind {Kind} failed", extra.Kind);
            }
        }
    }

    private async Task StopDataPortCoreAsync(CancellationToken cancellationToken)
    {
        if (_dataCts != null)
            try
            {
                await _dataCts.CancelAsync().ConfigureAwait(false);
            }
            catch
            {
                await _dataCts.CancelAsync();
            }

        if (_dataPortMultiplexer != null)
        {
            _dataPortMultiplexer.BleNetworkId.GotData -= OnBleNetworkIdReceived;
            try
            {
                await _dataPortMultiplexer.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            _dataPortMultiplexer = null;
        }

        foreach (var task in _dataReceiveTasks)
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }

        _dataReceiveTasks.Clear();
        _attachedAdditionalDataTransports.Clear();

        if (DataUdp != null)
        {
            var u = DataUdp;
            DataUdp = null;
            try
            {
                await UdpTransportFactory.ReleaseAsync(u, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        _dataCts?.Dispose();
        _dataCts = null;
        _currentDataPortUser = null;
    }

    /// <summary>Запускает фоновые P2P-сессии для всех чатов из БД (ещё не стартовавшие).</summary>
    public async Task EnsureAllChatSessionsStartedAsync(UserEntity user, AuthService auth, ChatRepository repo,
        SynchronizationContext? uiSync, CancellationToken cancellationToken = default)
    {
        var list = await repo.ListChatsAsync(user.Id).ConfigureAwait(false);
        foreach (var c in list)
            await TryEnsureChatSessionStartedAsync(c.Id, uiSync, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Запускает P2P-сессию для одного чата (invite и negotiation), если ещё не стартовала.
    ///     Вызывается после ручного Add chat, LAN discovery и входящего invite.
    /// </summary>
    public async Task TryEnsureChatSessionStartedAsync(int chatId, SynchronizationContext? uiSync,
        CancellationToken cancellationToken = default)
    {
        if (IsChatSessionStarted(chatId))
            return;

        var sem = _discoverySessionStartGates.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsChatSessionStarted(chatId))
                return;

            var user = _auth.CurrentUser;
            if (user == null)
                return;

            var chat = await _chats.GetChatAsync(chatId).ConfigureAwait(false);
            if (chat == null)
                return;
            if (await _chats.IsPeerBlockedAsync(user.Id, chat.PeerNetworkIdShort, cancellationToken)
                    .ConfigureAwait(false))
                return;

            if (IsChatSessionStarted(chatId))
                return;

            await EnsureStartedAsync(user, cancellationToken).ConfigureAwait(false);

            var session = GetSession(chat, user, _auth, _chats, uiSync);
            try
            {
                await session.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            MarkChatSessionStarted(chatId);
        }
        finally
        {
            sem.Release();
        }
    }

    private void OnBleNetworkIdReceived(object? sender, BleNetworkIdMessage msg)
    {
        if (msg.RemoteAddress.Kind != TransportKind.Bluetooth)
            return;
        var token = _dataCts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => HandleBleNetworkIdAsync(msg, token), token);
    }

    private async Task HandleBleNetworkIdAsync(BleNetworkIdMessage msg, CancellationToken cancellationToken)
    {
        try
        {
            var user = _auth.CurrentUser;
            if (user == null)
                return;
            if (msg.NetworkId.ToShortString() == user.NetworkIdShort)
                return;

            if (_bleDiscoveredPeerStore != null)
                await _bleDiscoveredPeerStore
                    .RecordDataPortNetworkIdAsync(msg.RemoteAddress, msg.NetworkId, cancellationToken)
                    .ConfigureAwait(false);

            LocalScan.RememberBluetoothPeer(msg.RemoteAddress);
            await LocalScan.SendUnicastPresencePingAsync(msg.RemoteAddress, cancellationToken).ConfigureAwait(false);

            var shortId = msg.NetworkId.ToShortString();
            var chat = await _chats.FindChatByPeerNetworkIdAsync(user.Id, shortId).ConfigureAwait(false);
            if (chat == null)
                return;

            var seenDirect = BluetoothTransportAddress.ToMacString(msg.RemoteAddress.Data);
            if (await _chats.ReplaceChatBluetoothMacAsync(chat.Id, seenDirect).ConfigureAwait(false))
            {
                _chats.NotifyChatListChanged();
                await RefreshSessionChatRowAsync(chat.Id, cancellationToken).ConfigureAwait(false);
            }

            await TryEnsureChatSessionStartedAsync(chat.Id, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
        catch
        {
            // safety: не ронять цикл приёма
        }
    }

    private void OnInviteReceived(object? sender, InviteMessage invite)
    {
        var token = _inviteCts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => HandleInviteAsync(invite, token), token);
    }

    private static async Task InviteReceiveLoopAsync(ITransport transport, InviteTransceiver transceiver,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var msg in transport.Inbound.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                transceiver.HandleIncoming(msg);
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
    }

    /// <summary>
    ///     Data-port receive loop. Host-provided transports (e.g. LoRa/Meshtastic) multiplex chat frames and
    ///     invite frames (0x30) on one channel: invites are routed to <see cref="HandleAdditionalInviteAsync" />
    ///     instead of the handshake/cipher multiplexer, which would drop them.
    /// </summary>
    private async Task DataPortReceiveLoopAsync(ITransport transport, DataPortMultiplexer multiplexer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var msg in transport.Inbound.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (IsInviteFromAdditionalTransport(transport, msg))
                    _ = Task.Run(() => HandleAdditionalInviteAsync(msg.Payload, msg.RemoteAddress));
                else
                    multiplexer.HandleIncoming(msg);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
    }

    private static bool IsInviteFromAdditionalTransport(ITransport transport, TransportReceiveMessage msg) =>
        transport.Kind is not (TransportKind.Udp or TransportKind.Bluetooth)
        && msg.Payload.Length > 0
        && msg.Payload.Span[0] == ChatInviteCodec.FrameChatInvite;

    private async Task HandleInviteAsync(InviteMessage invite, CancellationToken cancellationToken)
    {
        try
        {
            var udp = _inviteUdp;
            if (udp == null)
                return;
            if (IsOwnInviteDatagram(invite.RemoteAddress))
                return;

            // Не проталкивать cancellationToken в TryAccept: при остановке слушателя иначе можно прервать AddChat по пути.
            await AcceptInviteCoreAsync(invite.RawPayload, invite.RemoteAddress,
                async (payload, dest, _) => await udp.SendAsync(payload, dest, CancellationToken.None)
                    .ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            // safety: подписчик никогда не должен ронять цикл приёма транспивера
        }
    }

    /// <summary>
    ///     Invite arrived on a host transport (e.g. LoRa/Meshtastic): accept it and send the reply
    ///     invite back over the same transport kind.
    /// </summary>
    private async Task HandleAdditionalInviteAsync(ReadOnlyMemory<byte> payload, TransportAddress remoteAddress)
    {
        try
        {
            await AcceptInviteCoreAsync(payload, remoteAddress, SendInviteReplyViaAdditionalTransportAsync,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            // safety: цикл приёма host-транспорта не должен падать
            _logger.LogWarning(ex, "Failed to accept an invite from an additional transport (kind {Kind})",
                remoteAddress.Kind);
        }
    }

    private async Task AcceptInviteCoreAsync(ReadOnlyMemory<byte> datagram, TransportAddress remoteAddress,
        Func<ReadOnlyMemory<byte>, TransportAddress, CancellationToken, Task>? sendInviteReplyAsync,
        CancellationToken cancellationToken)
    {
        if (!ChatInviteCodec.TryParse(datagram.Span, out var initiator, out _, out _, out _, out _))
        {
            _logger.LogTrace("Dropped a malformed invite frame from kind {Kind}", remoteAddress.Kind);
            return;
        }

        await IncomingChatInviteHandler.TryAcceptAsync(datagram, _auth, _chats,
            sendInviteReplyAsync,
            remoteAddress,
            Settings,
            Settings.EnableBluetoothTransport ? Settings.SelectedBluetoothAdapterMac : null,
            CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation("Incoming chat invite from {Peer} via transport kind {Kind} accepted",
            initiator.ToShortString(), remoteAddress.Kind);

        var user = _auth.CurrentUser;
        if (user == null)
            return;
        var peerShort = initiator.ToShortString();
        if (await _chats.IsPeerBlockedAsync(user.Id, peerShort, cancellationToken).ConfigureAwait(false))
            return;
        var chat = await _chats.FindChatByPeerNetworkIdAsync(user.Id, peerShort).ConfigureAwait(false);
        if (chat == null)
            return;

        if (_sessionCache.IsStarted(chat.Id) && _sessionCache.TryGetSession(chat.Id, out var session))
        {
            await session.HandlePeerInviteResyncAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await TryEnsureChatSessionStartedAsync(chat.Id, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendInviteReplyViaAdditionalTransportAsync(ReadOnlyMemory<byte> payload,
        TransportAddress destination, CancellationToken cancellationToken)
    {
        var transport = _additionalTransports?.ResolveOutbound(destination);
        if (transport == null)
            throw new InvalidOperationException($"No outbound transport for destination kind {destination.Kind}.");
        await transport.SendAsync(payload, destination, CancellationToken.None).ConfigureAwait(false);
    }

    private void RefreshSelfUdpAddresses()
    {
        _selfUdpAddresses.Clear();
        foreach (var ip in LocalIPv4Resolver.GetAllUnicastIpv4Ordered())
            if (IPAddress.TryParse(ip, out var parsed))
                _selfUdpAddresses.Add(parsed.ToString());

        _selfUdpAddresses.Add(IPAddress.Loopback.ToString());
        _selfUdpAddresses.Add(IPAddress.Any.ToString());
    }

    private bool IsOwnInviteDatagram(TransportAddress remoteAddress)
    {
        if (remoteAddress.Kind != TransportKind.Udp)
            return false;
        var user = _auth.CurrentUser;
        if (user == null)
            return false;
        try
        {
            var ep = UdpTransportAddress.ToIPEndPoint(remoteAddress);
            return ep.Port == user.DataUdpPort && _selfUdpAddresses.Contains(ep.Address.ToString());
        }
        catch
        {
            return false;
        }
    }

    private async Task StopInviteListenerCoreAsync(CancellationToken cancellationToken)
    {
        if (_inviteCts != null)
            try
            {
                await _inviteCts.CancelAsync().ConfigureAwait(false);
            }
            catch
            {
                await _inviteCts.CancelAsync().ConfigureAwait(false);
            }

        if (Invite != null)
        {
            Invite.GotData -= OnInviteReceived;
            try
            {
                await Invite.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }

            Invite = null;
        }

        if (_inviteReceiveTasks != null)
        {
            try
            {
                await Task.WhenAll(_inviteReceiveTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }

            _inviteReceiveTasks.Clear();
        }

        if (_inviteUdp != null)
        {
            var u = _inviteUdp;
            _inviteUdp = null;
            try
            {
                await UdpTransportFactory.ReleaseAsync(u, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        _inviteCts?.Dispose();
        _inviteCts = null;
    }

    public async Task EnsureStartedAsync(UserEntity user, CancellationToken cancellationToken = default)
    {
        RefreshSelfUdpAddresses();
        var persisted = await _store.LoadAsync().ConfigureAwait(false);
        Settings.MaxSearchHops = persisted.MaxSearchHops;
        Settings.SendFailureSearchAttempts = persisted.SendFailureSearchAttempts;
        Settings.SendFailureRetryDelay = persisted.SendFailureRetryDelay;
        Settings.SearchWaitTimeout = persisted.SearchWaitTimeout;
        Settings.LinkTechnology = persisted.LinkTechnology;
        Settings.EnableUdpTransport = persisted.EnableUdpTransport;
        Settings.EnableBluetoothTransport = persisted.EnableBluetoothTransport;
        Settings.SelectedBluetoothAdapterDeviceId = persisted.SelectedBluetoothAdapterDeviceId;
        Settings.SelectedBluetoothAdapterMac = persisted.SelectedBluetoothAdapterMac;
        Settings.SuggestBluetoothPairing = persisted.SuggestBluetoothPairing;
        Settings.TrafficQuality = persisted.TrafficQuality;
        Settings.AdvertisedPeerCapabilities = persisted.AdvertisedPeerCapabilities | PresencePeerCapabilities.Chat;
        _bluetooth?.SetLocalNetworkId(CompressedNetworkId.FromShortString(user.NetworkIdShort));
        _bluetooth?.ApplySettings(Settings);

        // Инвайты (отдельный UDP) должны работать даже если presence/LAN bind на Android не удался.
        // SHO-10: a failed invite bind must not skip BLE and messenger servers.
        try
        {
            await EnsureInviteListenerRunningAsync(user, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error while establishing connection by UDP (invite listener)");
        }

        // Data UDP + handshake/cipher транспиверы на user.DataUdpPort.
        // UDP bind failure keeps BLE (when enabled) and still starts messenger servers below.
        try
        {
            await EnsureDataPortRunningAsync(user, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error while establishing connection by UDP (data port)");
        }

        MessengerServers?.Start();

        try
        {
            var localPeer = new PeerIdentity(user.Nickname,
                CompressedNetworkId.FromShortString(user.NetworkIdShort),
                user.DataUdpPort);
            await LocalScan.StartAsync(localPeer, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // LAN presence can fail (bind denied) while invite/data UDP already work.
            // Still start messenger-server long-poll so send/receive via servers stays up.
            MessengerServers?.Start();
            return;
        }

        if (!_discoveryHooked)
        {
            _presencePingWorkCts = new CancellationTokenSource();
            LocalScan.DiscoveryPingReceived += OnDiscoveryPingReceived;
            _discoveryHooked = true;
        }

        MessengerServers?.Start();
    }

    private void OnDiscoveryPingReceived(object? sender, DiscoveryPingReceivedEventArgs e)
    {
        var cts = _presencePingWorkCts;
        if (cts == null || cts.IsCancellationRequested)
            return;
        var token = cts.Token;
        _ = Task.Run(() => HandleDiscoveryPingAsync(e.Peer, token), token);
    }

    private async Task HandleDiscoveryPingAsync(DiscoveredLocalPeer peer, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        var user = _auth.CurrentUser;
        if (user == null)
            return;
        if (peer.TransportKind is not TransportKind.Udp and not TransportKind.Bluetooth)
            return;

        var shortId = peer.NetworkId.ToShortString();
        var chat = await _chats.FindChatByPeerNetworkIdAsync(user.Id, shortId).ConfigureAwait(false);
        if (chat == null)
            return;

        if (!string.IsNullOrWhiteSpace(peer.Nickname) &&
            await _chats.TryUpdatePeerNicknameAsync(chat.Id, peer.Nickname).ConfigureAwait(false))
            await RefreshSessionChatRowAsync(chat.Id, cancellationToken).ConfigureAwait(false);

        var seenDirect = peer.TransportKind == TransportKind.Udp
            ? UdpTransportAddress.ToIPEndPoint(peer.SourceAddress).Address.ToString()
            : BluetoothTransportAddress.ToMacString(peer.SourceAddress.Data);

        await ApplyDiscoveryPingRouteAsync(chat, peer.TransportKind, seenDirect, cancellationToken)
            .ConfigureAwait(false);
        await TryEnsureChatSessionStartedAsync(chat.Id, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Обновление маршрута из пинга; старт сессии выполняется отдельно
    ///     <see cref="TryEnsureChatSessionStartedAsync" />.
    /// </summary>
    private async Task ApplyDiscoveryPingRouteAsync(ChatEntity chat, byte pingKind, string seenDirect,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(chat.RelayRouteBlob))
        {
            var mergedRelay = PeerHostList.WithPrimaryFirst(chat.PeerHost, seenDirect);
            if (string.Equals(mergedRelay, chat.PeerHost, StringComparison.Ordinal))
                return;
            await _chats.UpdateChatP2pRouteAsync(chat.Id, mergedRelay, chat.PeerPort, chat.RelayRouteBlob)
                .ConfigureAwait(false);
            _chats.NotifyChatListChanged();
            await RefreshSessionChatRowAsync(chat.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        _sessionCache.TryGetSession(chat.Id, out var session);
        var started = _sessionCache.IsStarted(chat.Id);

        if (started && session != null)
        {
            if (pingKind == TransportKind.Udp)
            {
                var primary = PeerHostList.PrimaryHost(chat.PeerHost);
                if (string.Equals(primary, seenDirect, StringComparison.Ordinal))
                    return;
                await session.ApplyPeerEndpointAsync(seenDirect, chat.PeerPort, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var mergedBt = PeerHostList.MergeAppend(chat.PeerHost, seenDirect);
                if (string.Equals(mergedBt, chat.PeerHost, StringComparison.Ordinal))
                    return;
                await _chats.UpdateChatP2pRouteAsync(chat.Id, mergedBt, chat.PeerPort, null).ConfigureAwait(false);
                _chats.NotifyChatListChanged();
                await RefreshSessionChatRowAsync(chat.Id, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var mergedHost = pingKind == TransportKind.Bluetooth
            ? PeerHostList.MergeAppend(chat.PeerHost, seenDirect)
            : PeerHostList.WithPrimaryFirst(chat.PeerHost, seenDirect);
        if (string.Equals(mergedHost, chat.PeerHost, StringComparison.Ordinal))
            return;

        await _chats.UpdateChatP2pRouteAsync(chat.Id, mergedHost, chat.PeerPort, null).ConfigureAwait(false);
        _chats.NotifyChatListChanged();
        await RefreshSessionChatRowAsync(chat.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshSessionChatRowAsync(int chatId, CancellationToken cancellationToken)
    {
        var fresh = await _chats.GetChatAsync(chatId).ConfigureAwait(false);
        if (fresh == null)
            return;
        cancellationToken.ThrowIfCancellationRequested();
        if (_sessionCache.TryGetSession(chatId, out var session))
            session.ApplyChatRow(fresh);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (MessengerServers != null)
        {
            try
            {
                await MessengerServers.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        await StopInviteListenerAsync(cancellationToken).ConfigureAwait(false);

        await _dataPortGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopDataPortCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dataPortGate.Release();
        }

        if (_discoveryHooked)
        {
            LocalScan.DiscoveryPingReceived -= OnDiscoveryPingReceived;
            _discoveryHooked = false;
            try
            {
                var presenceCts = _presencePingWorkCts;
                if (presenceCts != null)
                    await presenceCts.CancelAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            _presencePingWorkCts?.Dispose();
            _presencePingWorkCts = null;
        }

        var sessions = _sessionCache.DrainAll();

        foreach (var kv in _discoverySessionStartGates.ToArray())
            if (_discoverySessionStartGates.TryRemove(kv.Key, out var g))
                try
                {
                    g.Dispose();
                }
                catch
                {
                    // ignore
                }

        foreach (var s in sessions)
            try
            {
                await s.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

        await LocalScan.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}