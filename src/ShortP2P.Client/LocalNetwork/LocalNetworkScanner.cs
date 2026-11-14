using System.Collections.Concurrent;
#if NETFRAMEWORK
using ShortP2P.Client.Compat;
#endif
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShortP2P.Auth.Data;
using ShortP2P.Discovery.Ble;
using ShortP2P.Discovery.Gossip;
using ShortP2P.Discovery.Pings;
using ShortP2P.Discovery.Profile;
using ShortP2P.Discovery.RouteTables;
using ShortP2P.Discovery.Transceivers;
using ShortP2P.Transport;
using ShortP2P.Transport.Abstractions;

namespace ShortP2P.Discovery;

/// <summary>
///     Discovery: UDP presence (<see cref="PresencePingCodec.UdpPort" />) на broadcast подсетей и limited broadcast,
///     плюс unicast на известные IPv4 (чаты, QR, публичный адрес этого узла); wire gossip/маршруты —
///     <see cref="UdpPeerDiscoveryOptions.DefaultDiscoveryUdpPort" /> на те же broadcast- и unicast-цели;
///     При включённом Bluetooth presence-пинг уходит на все известные BLE MAC (чаты, скан, сопряжённые устройства);
///     при ручном <see cref="ScanAsync" /> дополнительно — пассивное BLE-сканирование по UUID сервиса ShortP2P.
///     Приём на 0.0.0.0 — в том числе датаграммы с интернета при пробросе портов (NAT). Фоновая рассылка —
///     <see cref="LinkTechnologyPresetExtensions.GetPresencePingPeriod" />; ручное сканирование —
///     <see cref="ScanAsync" />, <see cref="TriggerScanAsync" />. Событие <see cref="DiscoveryPingReceived" /> —
///     по presence-пингам и по ответам gossip (Ack) на наши зонды.
/// </summary>
public sealed class LocalNetworkScanner(
    P2pRoutingSettings routingSettings,
    IUdpTransportFactory udpTransportFactory,
    Func<ITransport?>? getBluetoothTransport = null,
    IEnumerable<ITransport>? additionalDiscoveryTransports = null,
    IRouteTableSnapshotSource? routeTableSnapshotSource = null,
    IDiscoveryPingStore? discoveryPingStore = null,
    IBleShortP2PPeripheralScanner? blePeripheralScanner = null,
    IBleDiscoveredPeerStore? bleDiscoveredPeerStore = null,
    IBluetoothPresencePingTargetsProvider? bluetoothPresencePingTargetsProvider = null,
    IPeerProfileStore? peerProfileStore = null,
    ILocalPeerProfileSource? localPeerProfileSource = null,
    ILogger? logger = null) : IAsyncDisposable
{
    /// <summary>Длительность приёма пингов при ручном сканировании по умолчанию.</summary>
    public static readonly TimeSpan DefaultScanListenDuration = TimeSpan.FromSeconds(45);

    private static readonly TransportAddress MessengerServerPlaceholderAddress =
        new(TransportKind.MessengerServer, []);

    private readonly IEnumerable<ITransport>? _additionalDiscoveryTransports = additionalDiscoveryTransports;
    private readonly ConcurrentDictionary<string, TransportAddress> _bluetoothTargets = new();

    private readonly ConcurrentDictionary<CompressedNetworkId, DiscoveredLocalPeer> _entries = new();
    private readonly ConcurrentDictionary<CompressedNetworkId, byte> _messengerClientIds = new();
    private readonly Func<ITransport?>? _getBluetoothTransport = getBluetoothTransport;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly ConcurrentDictionary<long, CompressedNetworkId> _pendingProfileNonces = new();
    private readonly ConcurrentDictionary<CompressedNetworkId, byte> _profileRequestInFlight = new();
    private readonly List<PresencePingCodec.Transceiver> _secondaryPingTransceivers = [];
    private readonly List<Task> _secondaryPresenceReceiveTasks = [];
    private readonly object _snapshotSync = new();
    private readonly ConcurrentDictionary<string, string> _udpPresenceTargets = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _discoveryWireReceiveTask;
    private DiscoveryWireTransceiver? _discoveryWireTransceiver;
    private UdpTransport? _discoveryWireUdp;
    private long _lastGossipBroadcastNonce;
    private PeerIdentity? _localPeer;
    private DateTimeOffset _nextPublicIpv4PresenceLookupUtc = DateTimeOffset.MinValue;
    private Task? _periodicScanLoop;
    private PresencePingCodec.Transceiver? _presencePingTransceiver;

    /// <summary>Глубина вложенности раундов TX discovery; &gt;0 — приём пингов не обрабатывается.</summary>
    private int _presencePingTransmitDepth;

    private Task? _presenceReceiveTask;
    private UdpTransport? _presenceUdp;

    /// <summary>Два последних nonce gossip-зонда: поздние Ack после смены раунда всё ещё принимаются.</summary>
    private long _priorGossipBroadcastNonce;

    private volatile bool _scanSessionActive;
    private List<ITransport> _secondaryPresenceTransports = [];
    private IReadOnlyList<DiscoveredLocalPeer> _snapshot = [];
    private Task? _staleLoop;
    public bool IsUdpListening => _presenceUdp != null;
    public bool IsBluetoothListening { get; private set; }

    /// <summary>
    /// Optional prioritized discovery step (e.g. messenger servers) run before UDP/Bluetooth transports.
    /// </summary>
    public Func<CancellationToken, Task>? PrioritizedExternalDiscoveryRound { get; set; }

    /// <summary>
    /// Optional: request peer profile via messenger-server forward when peer is server-online and not on LAN.
    /// </summary>
    public Func<CompressedNetworkId, CancellationToken, Task>? RequestPeerProfileViaMessengerServer { get; set; }

    /// <summary>Удалять пира из списка, если не было пинга дольше этого (несколько периодов рассылки).</summary>
    private TimeSpan DiscoveryStaleAfter =>
        TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(45).Ticks,
            routingSettings.LinkTechnology.GetPresencePingPeriod(routingSettings.TrafficQuality).Ticks * 6));

    /// <summary>Снимок последних найденных пиров (кроме текущего пользователя).</summary>
    public IReadOnlyList<DiscoveredLocalPeer> Clients
    {
        get
        {
            lock (_snapshotSync)
            {
                return _snapshot;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Есть недавний presence-пинг с данным short network id (тот же порог, что и для «протухания» списка LAN).
    /// </summary>
    public bool IsPeerSeenRecentlyOnLan(string peerNetworkIdShort)
    {
        if (string.IsNullOrWhiteSpace(peerNetworkIdShort))
            return false;
        CompressedNetworkId id;
        try
        {
            id = CompressedNetworkId.FromShortString(peerNetworkIdShort.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        var cutoff = DateTimeOffset.UtcNow - DiscoveryStaleAfter;
        return _entries.TryGetValue(id, out var p) && p.LastSeenUtc >= cutoff;
    }

    public event EventHandler? ClientsChanged;

    /// <summary>
    /// Replaces messenger-server directory entries in the discovered list (self is skipped).
    /// LAN/Bluetooth rows for the same network id are kept and only annotated with server presence.
    /// </summary>
    public void ApplyMessengerServerDirectory(IReadOnlyList<MessengerServerDirectoryEntry> clients)
    {
        Require.NotNull(clients);
        var localPeer = _localPeer;
        var keepIds = new HashSet<CompressedNetworkId>();
        var changed = false;
        foreach (var key in _messengerClientIds.Keys.ToArray())
            _messengerClientIds.TryRemove(key, out _);

        foreach (var entry in clients)
        {
            if (!CompressedNetworkId.TryParseShortString(entry.NetworkIdShort, out var id) || id.IsEmpty)
                continue;
            if (localPeer != null && id == localPeer.NetworkId)
                continue;

            keepIds.Add(id);
            _messengerClientIds[id] = 0;
            var lastSeen = entry.LastSeenUtc == default
                ? DateTimeOffset.UtcNow
                : entry.LastSeenUtc.ToUniversalTime();
            var nick = string.IsNullOrWhiteSpace(entry.Nickname) ? "" : entry.Nickname.Trim();
            var peer = new DiscoveredLocalPeer(
                id,
                nick,
                MessengerServerPlaceholderAddress,
                TransportKind.MessengerServer,
                lastSeen,
                PresencePingCodec.DefaultDataUdpPort,
                MessengerServerOnline: entry.Online,
                AboutMe: entry.AboutMe ?? "",
                IsBot: entry.IsBot);
            _entries.TryGetValue(id, out var before);
            var stored = _entries.AddOrUpdate(id, peer, (_, existing) => MergeDiscoveredPeers(existing, peer));
            if (before is null || IsVisibleDirectoryChange(before, stored))
                changed = true;
            if (entry.Online && stored.TransportKind == TransportKind.MessengerServer && !stored.IsBot)
                SchedulePeerProfileRequest(stored);
        }

        foreach (var kv in _entries.ToArray())
        {
            if (kv.Value.TransportKind != TransportKind.MessengerServer)
                continue;
            if (keepIds.Contains(kv.Key) || kv.Value.IsBot)
                continue;
            if (_entries.TryRemove(kv.Key, out _))
                changed = true;
        }

        RebuildSnapshot();
        if (changed)
            ClientsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replaces bot rows from GET /bots. Bot-only messenger placeholders not in <paramref name="bots"/> are removed.
    /// A LAN/client row that is no longer a bot keeps its place and loses <see cref="DiscoveredLocalPeer.IsBot"/>.
    /// </summary>
    public void ApplyBotDirectory(IReadOnlyList<MessengerServerDirectoryEntry> bots)
    {
        Require.NotNull(bots);
        var localPeer = _localPeer;
        var keepIds = new HashSet<CompressedNetworkId>();
        var changed = false;

        foreach (var entry in bots)
        {
            if (!CompressedNetworkId.TryParseShortString(entry.NetworkIdShort, out var id) || id.IsEmpty)
                continue;
            if (localPeer != null && id == localPeer.NetworkId)
                continue;

            keepIds.Add(id);
            var nick = string.IsNullOrWhiteSpace(entry.Nickname) ? "" : entry.Nickname.Trim();
            var about = entry.AboutMe?.Trim() ?? "";
            var pub = entry.MessagePublicKey?.Trim() ?? "";
            var peer = new DiscoveredLocalPeer(
                id,
                nick,
                MessengerServerPlaceholderAddress,
                TransportKind.MessengerServer,
                DateTimeOffset.UtcNow,
                PresencePingCodec.DefaultDataUdpPort,
                MessengerServerOnline: false,
                AboutMe: about,
                IsBot: true,
                MessagePublicKey: pub);
            _entries.TryGetValue(id, out var before);
            var stored = _entries.AddOrUpdate(id, peer, (_, existing) => MergeDiscoveredPeers(existing, peer));
            if (before is null || IsVisibleDirectoryChange(before, stored))
                changed = true;
        }

        foreach (var kv in _entries.ToArray())
        {
            if (!kv.Value.IsBot || keepIds.Contains(kv.Key))
                continue;

            if (kv.Value.TransportKind == TransportKind.MessengerServer &&
                !_messengerClientIds.ContainsKey(kv.Key))
            {
                if (_entries.TryRemove(kv.Key, out _))
                    changed = true;
                continue;
            }

            _entries[kv.Key] = kv.Value with { IsBot = false };
            changed = true;
        }

        RebuildSnapshot();
        if (changed)
            ClientsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Принят чужой presence/discovery-пинг (не свой network id); обработчик не должен долго блокировать поток
    ///     приёма.
    /// </summary>
    public event EventHandler<DiscoveryPingReceivedEventArgs>? DiscoveryPingReceived;

    public async Task StartAsync(PeerIdentity localPeer, CancellationToken cancellationToken = default)
    {
        if (_cts != null)
            return;
        _localPeer = localPeer;
        _nextPublicIpv4PresenceLookupUtc = DateTimeOffset.MinValue;
        _secondaryPresenceTransports = BuildSecondaryPresenceTransports(_getBluetoothTransport?.Invoke(),
            _additionalDiscoveryTransports);
        EnsurePublicIpv4InPresenceTargets();
        _presenceUdp = udpTransportFactory.Acquire(IPAddress.Any, PresencePingCodec.UdpPort, true);
        _discoveryWireUdp = udpTransportFactory.Acquire(IPAddress.Any,
            UdpPeerDiscoveryOptions.DefaultDiscoveryUdpPort, true);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        await _presenceUdp.StartAsync(cancellationToken).ConfigureAwait(false);
        await _discoveryWireUdp.StartAsync(cancellationToken).ConfigureAwait(false);
        foreach (var transport in _secondaryPresenceTransports)
        {
            await transport.StartAsync(cancellationToken).ConfigureAwait(false);
            if (transport.Kind == TransportKind.Bluetooth)
                IsBluetoothListening = true;
        }

        _presencePingTransceiver = new PresencePingCodec.Transceiver(_presenceUdp);
        _presencePingTransceiver.GotData += OnPresencePingReceived;
        await _presencePingTransceiver.StartAsync(token).ConfigureAwait(false);
        _presenceReceiveTask = Task.Run(() => PresenceReceiveLoopAsync(_presenceUdp, _presencePingTransceiver, token),
            token);

        _discoveryWireTransceiver = new DiscoveryWireTransceiver(_discoveryWireUdp);
        _discoveryWireTransceiver.GotData += OnDiscoveryWireReceived;
        await _discoveryWireTransceiver.StartAsync(token).ConfigureAwait(false);
        _discoveryWireReceiveTask = Task.Run(
            () => DiscoveryWireReceiveLoopAsync(_discoveryWireUdp, _discoveryWireTransceiver, token), token);

        foreach (var transport in _secondaryPresenceTransports)
        {
            var tx = new PresencePingCodec.Transceiver(transport);
            tx.GotData += OnPresencePingReceived;
            await tx.StartAsync(token).ConfigureAwait(false);
            _secondaryPingTransceivers.Add(tx);
            _secondaryPresenceReceiveTasks.Add(Task.Run(() => PresenceReceiveLoopAsync(transport, tx, token), token));
        }

        _periodicScanLoop = Task.Run(() => PeriodicScanLoopAsync(token), token);
        _staleLoop = Task.Run(() => StaleLoopAsync(token), token);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts == null)
            return;

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_presencePingTransceiver != null)
        {
            _presencePingTransceiver.GotData -= OnPresencePingReceived;
            try
            {
                await _presencePingTransceiver.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }

            _presencePingTransceiver = null;
        }

        if (_discoveryWireTransceiver != null)
        {
            _discoveryWireTransceiver.GotData -= OnDiscoveryWireReceived;
            try
            {
                await _discoveryWireTransceiver.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }

            _discoveryWireTransceiver = null;
        }

        foreach (var tx in _secondaryPingTransceivers)
        {
            tx.GotData -= OnPresencePingReceived;
            try
            {
                await tx.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }

        _secondaryPingTransceivers.Clear();

        var activeLoops = new List<Task?>(_secondaryPresenceReceiveTasks.Count + 4)
        {
            _presenceReceiveTask,
            _discoveryWireReceiveTask,
            _periodicScanLoop,
            _staleLoop
        };
        activeLoops.AddRange(_secondaryPresenceReceiveTasks);
        var toWait = activeLoops.Where(t => t != null).ToArray();
        _presenceReceiveTask = null;
        _discoveryWireReceiveTask = null;
        _secondaryPresenceReceiveTasks.Clear();
        _periodicScanLoop = null;
        _staleLoop = null;
        if (toWait.Length > 0)
            try
            {
                await Task.WhenAll(toWait!).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }

        if (_presenceUdp != null)
        {
            var p = _presenceUdp;
            _presenceUdp = null;
            try
            {
                await udpTransportFactory.ReleaseAsync(p, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        if (_discoveryWireUdp != null)
        {
            var d = _discoveryWireUdp;
            _discoveryWireUdp = null;
            try
            {
                await udpTransportFactory.ReleaseAsync(d, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        _cts.Dispose();
        _cts = null;
        _localPeer = null;
        _entries.Clear();
        _bluetoothTargets.Clear();
        _udpPresenceTargets.Clear();
        IsBluetoothListening = false;
        lock (_snapshotSync)
        {
            _snapshot = [];
        }

        foreach (var transport in _secondaryPresenceTransports)
            try
            {
                await transport.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
    }

    public void RememberBluetoothPeer(TransportAddress address)
    {
        if (address.Kind != TransportKind.Bluetooth || address.Data.Length == 0)
            return;
        _bluetoothTargets[Convert.ToBase64String(address.Data)] = address;
    }

    /// <summary>
    ///     Unicast presence ping на конкретный BLE-адрес (ответ на полученный NetworkId).
    /// </summary>
    public async Task SendUnicastPresencePingAsync(TransportAddress bluetoothDestination,
        CancellationToken cancellationToken = default)
    {
        if (!routingSettings.VisibleForSearch)
            return;
        if (bluetoothDestination.Kind != TransportKind.Bluetooth)
            return;
        if (!IsTransportEnabled(TransportKind.Bluetooth))
            return;

        var localPeer = _localPeer;
        var bt = _secondaryPresenceTransports.FirstOrDefault(t => t.Kind == TransportKind.Bluetooth);
        if (localPeer == null || bt == null)
            return;

        RememberBluetoothPeer(bluetoothDestination);
        var caps = routingSettings.AdvertisedPeerCapabilities | PresencePeerCapabilities.Chat;
        var payload = PresencePingCodec.Build(
            localPeer.NetworkId,
            localPeer.Nickname,
            localPeer.DataUdpPort,
            routingSettings.LinkTechnology,
            caps);
        try
        {
            await bt.SendAsync(payload, bluetoothDestination, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // peer may be out of range
        }
    }

    /// <summary>
    ///     Запоминает IP-адрес пира, чтобы отправлять presence-пинги напрямую (в т.ч. для non-LAN адресов после Add/QR).
    /// </summary>
    public void RememberUdpPresenceTarget(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return;
        host = host.Trim();
        if (!IPAddress.TryParse(host, out var ip))
            return;
        if (IPAddress.IsLoopback(ip))
            return;
        var normalized = ip.ToString();
        _udpPresenceTargets[normalized] = normalized;
    }

    private bool ShouldRunBleDiscoveryScan()
    {
        return blePeripheralScanner != null
               && IsTransportEnabled(TransportKind.Bluetooth)
               && _secondaryPresenceTransports.Any(t => t.Kind == TransportKind.Bluetooth);
    }

    private bool IsTransportEnabled(byte kind)
    {
        return kind switch
        {
            TransportKind.Udp => routingSettings.EnableUdpTransport,
            TransportKind.Bluetooth => routingSettings.EnableBluetoothTransport,
            _ => true
        };
    }

    /// <summary>Один раунд discovery: UDP broadcast по каждой подсети (+ limited broadcast).</summary>
    public Task TriggerScanAsync(CancellationToken cancellationToken = default)
    {
        return SendDiscoveryBroadcastRoundAsync(cancellationToken);
    }

    /// <summary>
    ///     Ручное сканирование: очистка списка, раунд broadcast discovery, приём пингов <paramref name="listenDuration" />,
    ///     затем одно обновление <see cref="Clients" /> и событие <see cref="ClientsChanged" />.
    /// </summary>
    public async Task ScanAsync(TimeSpan listenDuration, CancellationToken cancellationToken = default)
    {
        if (listenDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(listenDuration));
     
        _scanSessionActive = true;
        try
        {
            // Drop LAN/BT rows for a fresh listen; keep messenger-server directory until GetClients refreshes it.
            foreach (var kv in _entries.ToArray())
            {
                if (kv.Value.TransportKind == TransportKind.MessengerServer)
                    continue;
                _entries.TryRemove(kv.Key, out _);
            }

            _pendingProfileNonces.Clear();
            _profileRequestInFlight.Clear();

            RebuildSnapshot();
            ClientsChanged?.Invoke(this, EventArgs.Empty);

            var sw = Stopwatch.StartNew();
            if (ShouldRunBleDiscoveryScan())
            {
                var blePart = TimeSpan.FromSeconds(
#if NETFRAMEWORK
                    Math.Min(20, Math.Max(4, listenDuration.TotalSeconds * 0.35)));
#else
                    Math.Clamp(listenDuration.TotalSeconds * 0.35, 4, 20));
#endif
                if (blePart > listenDuration)
                    blePart = listenDuration;
                try
                {
                    await blePeripheralScanner!.ScanAsync(blePart,
                        (addr, scanResult) =>
                        {
                            RememberBluetoothPeer(addr);
                            _ = bleDiscoveredPeerStore?.RecordScanSeenAsync(addr, scanResult, cancellationToken);
                        }, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // BLE scan unavailable / permission
                }
            }

            // GetClients (PrioritizedExternalDiscoveryRound) must run before the LAN listen window.
            await SendDiscoveryBroadcastRoundAsync(cancellationToken).ConfigureAwait(false);
            var remaining = listenDuration - sw.Elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _scanSessionActive = false;
        }

        RebuildSnapshot();
        ClientsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPresencePingReceived(object? sender, PingMessage msg)
    {
        // if (Volatile.Read(ref _presencePingTransmitDepth) != 0)
        //     return;
        if (!IsTransportEnabled(msg.RemoteAddress.Kind))
            return;

        var localPeer = _localPeer;
        if (localPeer == null)
            return;
        if (msg.PeerNetworkId == localPeer.NetworkId)
            return;

        if (msg.RemoteAddress.Kind == TransportKind.Bluetooth)
        {
            RememberBluetoothPeer(msg.RemoteAddress);
            if (bleDiscoveredPeerStore != null)
            {
                var nick = string.IsNullOrWhiteSpace(msg.Nickname) ? "?" : msg.Nickname.Trim();
                _ = bleDiscoveredPeerStore.RecordPingAsync(msg.RemoteAddress, msg.PeerNetworkId, nick,
                    CancellationToken.None);
            }
        }

        var peer = new DiscoveredLocalPeer(msg.PeerNetworkId, msg.Nickname, msg.RemoteAddress,
            msg.RemoteAddress.Kind, DateTimeOffset.UtcNow, msg.PeerDataUdpPort, msg.AdvertisedLink,
            msg.AdvertisedCapabilities);

        OnDiscoveryPingReceived(peer);
        DiscoveryPingReceived?.Invoke(this, new DiscoveryPingReceivedEventArgs(peer));
        SchedulePeerProfileRequest(peer);
    }

    private static DiscoveredLocalPeer MergeDiscoveredPeers(DiscoveredLocalPeer existing, DiscoveredLocalPeer incoming)
    {
        var lastSeen = incoming.LastSeenUtc > existing.LastSeenUtc ? incoming.LastSeenUtc : existing.LastSeenUtc;
        var nick = incoming.IsBot && !string.IsNullOrEmpty(incoming.Nickname)
            ? incoming.Nickname
            : !string.IsNullOrEmpty(existing.Nickname) ? existing.Nickname : incoming.Nickname;
        var serverOnline = existing.MessengerServerOnline || incoming.MessengerServerOnline;
        var aboutMe = !string.IsNullOrEmpty(incoming.AboutMe) ? incoming.AboutMe : existing.AboutMe;
        var isBot = existing.IsBot || incoming.IsBot;
        var messagePublicKey = !string.IsNullOrEmpty(incoming.MessagePublicKey)
            ? incoming.MessagePublicKey
            : existing.MessagePublicKey;

        if (existing.TransportKind is TransportKind.Udp or TransportKind.Bluetooth &&
            incoming.TransportKind == TransportKind.MessengerServer)
        {
            return existing with
            {
                LastSeenUtc = lastSeen,
                Nickname = nick,
                MessengerServerOnline = incoming.MessengerServerOnline,
                AboutMe = aboutMe,
                IsBot = isBot,
                MessagePublicKey = messagePublicKey
            };
        }

        if (existing.TransportKind == TransportKind.MessengerServer &&
            incoming.TransportKind is TransportKind.Udp or TransportKind.Bluetooth)
        {
            return incoming with
            {
                LastSeenUtc = lastSeen,
                Nickname = string.IsNullOrEmpty(incoming.Nickname) ? existing.Nickname : incoming.Nickname,
                MessengerServerOnline = existing.MessengerServerOnline,
                AboutMe = aboutMe,
                IsBot = isBot,
                MessagePublicKey = messagePublicKey
            };
        }

        return existing.TransportKind switch
        {
            TransportKind.Udp when incoming.TransportKind == TransportKind.Bluetooth => existing with
            {
                LastSeenUtc = lastSeen,
                Nickname = nick,
                MessengerServerOnline = serverOnline,
                AboutMe = aboutMe,
                IsBot = isBot,
                MessagePublicKey = messagePublicKey
            },
            TransportKind.Bluetooth when incoming.TransportKind == TransportKind.Udp => incoming with
            {
                LastSeenUtc = lastSeen,
                Nickname = string.IsNullOrEmpty(incoming.Nickname) ? existing.Nickname : incoming.Nickname,
                MessengerServerOnline = serverOnline,
                AboutMe = aboutMe,
                IsBot = isBot,
                MessagePublicKey = messagePublicKey
            },
            _ => (incoming.LastSeenUtc >= existing.LastSeenUtc ? incoming : existing) with
            {
                Nickname = nick,
                MessengerServerOnline = serverOnline,
                AboutMe = aboutMe,
                IsBot = isBot,
                MessagePublicKey = messagePublicKey
            }
        };
    }

    private static async Task PresenceReceiveLoopAsync(ITransport transport, PresencePingCodec.Transceiver transceiver,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var msg in transport.Inbound.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                transceiver.HandleIncoming(msg);
                await Task.Delay(500, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
    }

    private void OnDiscoveryWireReceived(object? sender, DiscoveryWireMessage msg)
    {
        if (Volatile.Read(ref _presencePingTransmitDepth) != 0)
            return;
        if (!IsTransportEnabled(TransportKind.Udp))
            return;

        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => HandleDiscoveryWireAsync(msg, token), token);
    }

    private static async Task DiscoveryWireReceiveLoopAsync(ITransport transport, DiscoveryWireTransceiver transceiver,
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

    private async Task HandleDiscoveryWireAsync(DiscoveryWireMessage msg, CancellationToken cancellationToken)
    {
        try
        {
            var wireUdp = _discoveryWireUdp;
            if (wireUdp == null)
                return;
            var buf = msg.RawPayload.ToArray();
            switch (msg.Kind)
            {
                case DiscoveryWireKind.GossipAck:
                    TryHandleGossipAckAsDiscovery(buf, msg.RemoteAddress);
                    return;
                case DiscoveryWireKind.GossipProbe:
                    await TryReplyToGossipProbeAsync(wireUdp, buf, msg.RemoteAddress, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                case DiscoveryWireKind.RouteTableRequest:
                    await TryReplyToRouteTableRequestAsync(wireUdp, buf, msg.RemoteAddress, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                case DiscoveryWireKind.RouteTableReply:
                    // Reply парсится потребителем (Discovery), здесь — только ack-стейт через DiscoveryPingReceived при необходимости.
                    return;
                case DiscoveryWireKind.PeerProfileRequest:
                    await TryReplyToPeerProfileRequestAsync(wireUdp, buf, msg.RemoteAddress, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                case DiscoveryWireKind.PeerProfileReply:
                    await TryHandlePeerProfileReplyAsync(buf, cancellationToken).ConfigureAwait(false);
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discovery wire handling failed (best-effort); continuing");
        }
    }

    /// <summary>Ответ на наш gossip-зонд (в т.ч. с внешнего IP): превращаем в тот же поток, что и presence.</summary>
    private bool TryHandleGossipAckAsDiscovery(byte[] buf, TransportAddress remote)
    {
        if (!GossipWireCodec.TryParseAck(buf, out var nonce, out var responderId, out var dataPort, out var nick))
            return false;
        if (!IsRecentGossipBroadcastNonce(nonce))
            return false;
        if (dataPort is < 1 or > 65535)
            return false;

        var localPeer = _localPeer;
        if (localPeer != null && responderId == localPeer.NetworkId)
            return true;

        if (remote.Kind != TransportKind.Udp)
            return true;

        IPEndPoint remoteEp;
        try
        {
            remoteEp = UdpTransportAddress.ToIPEndPoint(remote);
        }
        catch
        {
            return true;
        }

        var dataAddr = UdpTransportAddress.FromIPEndPoint(new IPEndPoint(remoteEp.Address, dataPort));
        nick = string.IsNullOrWhiteSpace(nick) ? "?" : nick.Trim();
        var peer = new DiscoveredLocalPeer(responderId, nick, dataAddr, TransportKind.Udp, DateTimeOffset.UtcNow,
            dataPort);
        OnDiscoveryPingReceived(peer);
        DiscoveryPingReceived?.Invoke(this, new DiscoveryPingReceivedEventArgs(peer));
        SchedulePeerProfileRequest(peer);
        return true;
    }

    /// <summary>
    ///     Best-effort: подтянуть кэш и запросить Avatar/AboutMe у пира по discovery wire или server forward.
    ///     Ошибки только логируются — скан и discovery не блокируются.
    /// </summary>
    private void SchedulePeerProfileRequest(DiscoveredLocalPeer peer)
    {
        var viaLan = peer.TransportKind is TransportKind.Udp or TransportKind.Bluetooth;
        var viaServer = peer.TransportKind == TransportKind.MessengerServer && peer.MessengerServerOnline;
        if (!viaLan && !viaServer)
            return;
        if (!_profileRequestInFlight.TryAdd(peer.NetworkId, 0))
            return;

        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await TryHydrateAboutMeFromStoreAsync(peer.NetworkId, token).ConfigureAwait(false);
                if (peer.TransportKind == TransportKind.Udp)
                    await RequestPeerProfileAsync(peer, token).ConfigureAwait(false);
                else if (viaServer)
                {
                    var forward = RequestPeerProfileViaMessengerServer;
                    if (forward != null)
                        await forward(peer.NetworkId, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // stop / dispose
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Peer profile request failed for {NetworkId} (best-effort); continuing scan",
                    peer.NetworkId.ToShortString());
            }
            finally
            {
                _profileRequestInFlight.TryRemove(peer.NetworkId, out _);
            }
        }, token);
    }

    /// <summary>Обновить AboutMe в snapshot после server-forward reply (best-effort).</summary>
    public void ApplyCachedAboutMe(CompressedNetworkId networkId, string aboutMe)
    {
        aboutMe ??= "";
        if (!_entries.TryGetValue(networkId, out var existing))
            return;
        if (string.Equals(existing.AboutMe, aboutMe, StringComparison.Ordinal))
            return;

        _entries[networkId] = existing with { AboutMe = aboutMe };
        if (!_scanSessionActive)
        {
            RebuildSnapshot();
            ClientsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task TryHydrateAboutMeFromStoreAsync(CompressedNetworkId networkId, CancellationToken cancellationToken)
    {
        if (peerProfileStore == null)
            return;
        try
        {
            var snap = await peerProfileStore.GetAsync(networkId, cancellationToken).ConfigureAwait(false);
            if (snap == null || string.IsNullOrEmpty(snap.AboutMe))
                return;
            if (_entries.TryGetValue(networkId, out var existing) && string.IsNullOrEmpty(existing.AboutMe))
            {
                _entries[networkId] = existing with { AboutMe = snap.AboutMe };
                if (!_scanSessionActive)
                {
                    RebuildSnapshot();
                    ClientsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Peer profile cache read failed for {NetworkId} (best-effort); continuing",
                networkId.ToShortString());
        }
    }

    private async Task RequestPeerProfileAsync(DiscoveredLocalPeer peer, CancellationToken cancellationToken)
    {
        var wireUdp = _discoveryWireUdp;
        var localPeer = _localPeer;
        if (wireUdp == null || localPeer == null)
            return;

        IPEndPoint remoteEp;
        try
        {
            remoteEp = UdpTransportAddress.ToIPEndPoint(peer.SourceAddress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Peer profile: cannot resolve UDP endpoint for {NetworkId}; skipping",
                peer.NetworkId.ToShortString());
            return;
        }

        var dest = UdpTransportAddress.FromIPEndPoint(new IPEndPoint(remoteEp.Address, GossipWireCodec.UdpPort));
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
        _pendingProfileNonces[nonce] = peer.NetworkId;
        try
        {
            var request = PeerProfileWireCodec.BuildRequest(nonce, localPeer.NetworkId);
            await wireUdp.SendAsync(request, dest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pendingProfileNonces.TryRemove(nonce, out _);
            _logger.LogWarning(ex,
                "Peer profile request send failed for {NetworkId} (best-effort)",
                peer.NetworkId.ToShortString());
        }
    }

    private async Task TryReplyToPeerProfileRequestAsync(ITransport replyTransport, byte[] buf,
        TransportAddress remote, CancellationToken cancellationToken)
    {
        try
        {
            if (!PeerProfileWireCodec.TryParseRequest(buf, out var nonce, out var sender))
                return;

            var localPeer = _localPeer;
            if (localPeer == null)
                return;
            if (sender == localPeer.NetworkId)
                return;

            string aboutMe = "";
            byte[]? avatar = null;
            if (localPeerProfileSource != null)
            {
                try
                {
                    (aboutMe, avatar) = await localPeerProfileSource.GetLocalProfileAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Local profile source failed while answering peer profile request");
                }
            }

            var reply = PeerProfileWireCodec.BuildReply(nonce, localPeer.NetworkId, aboutMe, avatar);
            await replyTransport.SendAsync(reply, remote, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Peer profile reply failed (best-effort); continuing");
        }
    }

    private async Task TryHandlePeerProfileReplyAsync(byte[] buf, CancellationToken cancellationToken)
    {
        try
        {
            if (!PeerProfileWireCodec.TryParseReply(buf, out var nonce, out var responderId, out var aboutMe,
                    out var avatar))
            {
                _logger.LogWarning("Peer profile reply parse failed (best-effort); ignoring datagram");
                return;
            }

            if (!_pendingProfileNonces.TryRemove(nonce, out var expectedId) || expectedId != responderId)
            {
                // чужой/просроченный nonce — игнорируем без ошибки скана
                return;
            }

            if (peerProfileStore != null)
            {
                try
                {
                    string? nick = null;
                    if (_entries.TryGetValue(responderId, out var known))
                        nick = known.Nickname;
                    await peerProfileStore.UpsertAsync(responderId, nick, aboutMe, avatar, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Peer profile store write failed for {NetworkId} (best-effort); continuing",
                        responderId.ToShortString());
                }
            }

            if (_entries.TryGetValue(responderId, out var existing))
            {
                _entries[responderId] = existing with { AboutMe = aboutMe ?? "" };
                if (!_scanSessionActive)
                {
                    RebuildSnapshot();
                    ClientsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Peer profile reply handling failed (best-effort); continuing");
        }
    }

    private void RegisterGossipBroadcastNonce(long nonce)
    {
        var prev = Interlocked.Exchange(ref _lastGossipBroadcastNonce, nonce);
        Volatile.Write(ref _priorGossipBroadcastNonce, prev);
    }

    private bool IsRecentGossipBroadcastNonce(long n)
    {
        return n == Volatile.Read(ref _lastGossipBroadcastNonce) || n == Volatile.Read(ref _priorGossipBroadcastNonce);
    }

    private async Task<bool> TryReplyToGossipProbeAsync(ITransport replyTransport, byte[] buf,
        TransportAddress remote, CancellationToken cancellationToken)
    {
        if (!GossipWireCodec.TryParseProbe(buf, out var nonce, out var sender, out var target))
            return false;

        var localPeer = _localPeer;
        if (localPeer == null)
            return true;

        // Hidden from search: consume the probe without advertising ourselves.
        if (!routingSettings.VisibleForSearch)
            return true;

        var myId = localPeer.NetworkId;

        if (sender == myId)
            return true;

        if (!target.IsEmpty && target != myId)
            return true;

        var nick = string.IsNullOrWhiteSpace(localPeer.Nickname) ? "?" : localPeer.Nickname.Trim();
        var port = localPeer.DataUdpPort is >= 1 and <= 65535
            ? localPeer.DataUdpPort
            : PresencePingCodec.DefaultDataUdpPort;
        var ack = GossipWireCodec.BuildAck(nonce, myId, port, nick);
        await replyTransport.SendAsync(ack, remote, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryReplyToRouteTableRequestAsync(ITransport replyTransport, byte[] buf,
        TransportAddress remote, CancellationToken cancellationToken)
    {
        if (!RouteTableWireCodec.TryParseRequest(buf, out var nonce, out var sender))
            return false;

        var localPeer = _localPeer;
        if (localPeer == null)
            return true;
        var myId = localPeer.NetworkId;

        if (sender == myId)
            return true;

        var caps = routingSettings.AdvertisedPeerCapabilities | PresencePeerCapabilities.Chat;
        if (!caps.HasFlag(PresencePeerCapabilities.PeerSearch))
            return true;

        IReadOnlyList<Route> routes = Array.Empty<Route>();
        if (routeTableSnapshotSource != null)
            try
            {
                routes = await routeTableSnapshotSource.GetRoutesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                routes = Array.Empty<Route>();
            }

        var reply = RouteTableWireCodec.BuildReply(nonce, myId, routes);
        await replyTransport.SendAsync(reply, remote, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void OnDiscoveryPingReceived(DiscoveredLocalPeer peer)
    {
        discoveryPingStore?.Write(
            new PeerIdentity(peer.Nickname, peer.NetworkId, peer.PeerDataUdpPort),
            peer.SourceAddress, peer.LastSeenUtc);

        _entries.TryGetValue(peer.NetworkId, out var before);
        var merged = _entries.AddOrUpdate(peer.NetworkId, peer, (_, existing) => MergeDiscoveredPeers(existing, peer));
        if (_scanSessionActive)
            return;

        // Always refresh snapshot LastSeen; raise ClientsChanged only on visible directory changes
        // or when a peer returns after a quiet gap (online transition) — not on every presence tick.
        RebuildSnapshot();
        if (!ShouldRaiseClientsChanged(before, merged))
            return;
        ClientsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     True for new peers, identity/route/profile changes, or a return after
    ///     <see cref="DirectoryNotifyQuietPeriod"/> — not for LastSeen-only refreshes.
    /// </summary>
    private bool ShouldRaiseClientsChanged(DiscoveredLocalPeer? before, DiscoveredLocalPeer after)
    {
        if (before is null)
            return true;
        if (IsVisibleDirectoryChange(before, after))
            return true;
        return DateTimeOffset.UtcNow - before.LastSeenUtc > DirectoryNotifyQuietPeriod;
    }

    private TimeSpan DirectoryNotifyQuietPeriod =>
        TimeSpan.FromTicks(Math.Max(
            TimeSpan.FromSeconds(15).Ticks,
            routingSettings.LinkTechnology.GetPresencePingPeriod(routingSettings.TrafficQuality).Ticks * 2));

    private static bool IsVisibleDirectoryChange(DiscoveredLocalPeer before, DiscoveredLocalPeer after)
    {
        if (before.TransportKind != after.TransportKind)
            return true;
        if (before.PeerDataUdpPort != after.PeerDataUdpPort)
            return true;
        if (before.MessengerServerOnline != after.MessengerServerOnline)
            return true;
        if (before.IsBot != after.IsBot)
            return true;
        if (before.AdvertisedLinkTechnology != after.AdvertisedLinkTechnology)
            return true;
        if (before.AdvertisedCapabilities != after.AdvertisedCapabilities)
            return true;
        if (!string.Equals(before.Nickname, after.Nickname, StringComparison.Ordinal))
            return true;
        if (!string.Equals(before.AboutMe, after.AboutMe, StringComparison.Ordinal))
            return true;
        if (!string.Equals(before.MessagePublicKey, after.MessagePublicKey, StringComparison.Ordinal))
            return true;
        if (before.SourceAddress.Kind != after.SourceAddress.Kind)
            return true;
        return !before.SourceAddress.Data.AsSpan().SequenceEqual(after.SourceAddress.Data);
    }

    private void RebuildSnapshot()
    {
        var list = _entries.Values
            .OrderBy(p => p.Nickname, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.NetworkId)
            .ToList();
        lock (_snapshotSync)
        {
            _snapshot = list;
        }
    }

    /// <summary>
    ///     Добавляет публичный IPv4 в цели unicast-пингов (hairpin/NAT и пиры, достижимые по белому адресу).
    ///     HTTP-запрос ограничен по частоте.
    /// </summary>
    private void EnsurePublicIpv4InPresenceTargets()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextPublicIpv4PresenceLookupUtc)
            return;
        _nextPublicIpv4PresenceLookupUtc = now.AddMinutes(3);
        try
        {
            var pub = LocalIPv4Resolver.TryGetPublicIpv4(TimeSpan.FromSeconds(1));
            if (pub is not null && !string.IsNullOrWhiteSpace(pub))
                RememberUdpPresenceTarget(pub.Trim());
        }
        catch
        {
            // сеть / таймаут echo-сервиса
        }
    }

    private async Task SendDiscoveryBroadcastRoundAsync(CancellationToken cancellationToken)
    {
        var external = PrioritizedExternalDiscoveryRound;
        if (external != null)
        {
            try
            {
                await external(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // servers / external discovery must not block UDP/BT rounds
            }
        }

        // Still refresh GetClients directory above; do not advertise presence when hidden.
        if (!routingSettings.VisibleForSearch)
            return;

        var localPeer = _localPeer;
        var presenceUdp = _presenceUdp;
        if (localPeer == null) return;
        Interlocked.Increment(ref _presencePingTransmitDepth);
        try
        {
            EnsurePublicIpv4InPresenceTargets();
            var caps = routingSettings.AdvertisedPeerCapabilities | PresencePeerCapabilities.Chat;
            var payload = PresencePingCodec.Build(
                localPeer.NetworkId,
                localPeer.Nickname,
                localPeer.DataUdpPort,
                routingSettings.LinkTechnology,
                caps);

            if (presenceUdp != null && IsTransportEnabled(TransportKind.Udp))
            {
                var broadcastEps = LanBroadcastHelper.GetIpv4BroadcastEndpoints(PresencePingCodec.UdpPort);
                foreach (var ep in broadcastEps)
                    try
                    {
                        var addr = UdpTransportAddress.FromIPEndPoint(ep);
                        await presenceUdp.SendAsync(payload, addr, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // один broadcast может быть недоступен
                    }

                foreach (var host in _udpPresenceTargets.Values)
                    try
                    {
                        if (!IPAddress.TryParse(host, out var ip))
                            continue;
                        var addr = UdpTransportAddress.FromIPEndPoint(new IPEndPoint(ip, PresencePingCodec.UdpPort));
                        await presenceUdp.SendAsync(payload, addr, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // peer может быть офлайн / IP устарел
                    }
            }

            var bt = _secondaryPresenceTransports.FirstOrDefault(t => t.Kind == TransportKind.Bluetooth);
            if (bt != null && IsTransportEnabled(TransportKind.Bluetooth))
            {
                try
                {
                    if (bluetoothPresencePingTargetsProvider != null)
                    {
                        var fromProvider = await bluetoothPresencePingTargetsProvider
                            .GetBluetoothPingTargetsAsync(cancellationToken).ConfigureAwait(false);
                        foreach (var addr in fromProvider)
                            RememberBluetoothPeer(addr);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // БД / Bluetooth: не блокируем UDP-раунд
                }

                if (_bluetoothTargets.IsEmpty)
                    try
                    {
                        var paired = await TryGetPairedBluetoothAddressesAsync(bt, cancellationToken)
                            .ConfigureAwait(false);
                        foreach (var addr in paired)
                            RememberBluetoothPeer(addr);
                    }
                    catch
                    {
                        // bluetooth subsystem unavailable
                    }

                foreach (var target in _bluetoothTargets.Values)
                    try
                    {
                        await bt.SendAsync(payload, target, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // устройство выключено/вне зоны/не сопряжено
                    }
            }

            foreach (var transport in _secondaryPresenceTransports.Where(t => t.Kind != TransportKind.Bluetooth))
            {
                if (!IsTransportEnabled(transport.Kind))
                    continue;
                try
                {
                    await transport.SendAsync(payload, new TransportAddress(transport.Kind, []), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // транспорт может требовать предварительной доступности канала/устройства
                }
            }

            await SendGossipDiscoveryProbesAsync(localPeer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _presencePingTransmitDepth);
        }
    }

    /// <summary>
    ///     Gossip-зонды на wire-порт: те же broadcast, что LAN, и unicast на известные внешние/внутренние IPv4.
    /// </summary>
    private async Task SendGossipDiscoveryProbesAsync(PeerIdentity localPeer, CancellationToken cancellationToken)
    {
        var wireUdp = _discoveryWireUdp;
        if (wireUdp == null || !IsTransportEnabled(TransportKind.Udp))
            return;

        long nonce;
        do
        {
#if NETFRAMEWORK
            var bytes = new byte[8];
            new Random().NextBytes(bytes);
            nonce = BitConverter.ToInt64(bytes, 0);
#else
            nonce = Random.Shared.NextInt64();
#endif
        } while (nonce == 0);

        RegisterGossipBroadcastNonce(nonce);
        var probe = GossipWireCodec.BuildProbe(nonce, localPeer.NetworkId, CompressedNetworkId.Empty);

        foreach (var ep in LanBroadcastHelper.GetIpv4BroadcastEndpoints(GossipWireCodec.UdpPort))
            try
            {
                var addr = UdpTransportAddress.FromIPEndPoint(ep);
                await wireUdp.SendAsync(probe, addr, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // один broadcast может быть недоступен
            }

        foreach (var host in _udpPresenceTargets.Values)
            try
            {
                if (!IPAddress.TryParse(host, out var ip))
                    continue;
                if (IPAddress.IsLoopback(ip))
                    continue;
                var addr = UdpTransportAddress.FromIPEndPoint(new IPEndPoint(ip, GossipWireCodec.UdpPort));
                await wireUdp.SendAsync(probe, addr, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // узел недоступен или фильтрация
            }
    }

    private static async Task<IReadOnlyList<TransportAddress>> TryGetPairedBluetoothAddressesAsync(
        ITransport transport, CancellationToken cancellationToken)
    {
        var m = transport.GetType().GetMethod("GetPairedDeviceAddressesAsync",
            [typeof(CancellationToken)]);
        if (m == null || m.Invoke(transport, [cancellationToken]) is not Task t)
            return [];
        await t.ConfigureAwait(false);
        var prop = t.GetType().GetProperty("Result");
        return prop?.GetValue(t) as IReadOnlyList<TransportAddress> ?? [];
    }

    private async Task PeriodicScanLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var period = routingSettings.LinkTechnology.GetPresencePingPeriod(routingSettings.TrafficQuality);
            try
            {
                await SendDiscoveryBroadcastRoundAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(period, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(period, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task StaleLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var cutoff = DateTimeOffset.UtcNow - DiscoveryStaleAfter;
            var removed = false;
            foreach (var kv in _entries.ToArray())
            {
                if (kv.Value.TransportKind == TransportKind.MessengerServer)
                    continue;
                if (kv.Value.LastSeenUtc >= cutoff) continue;
                if (_entries.TryRemove(kv.Key, out _))
                    removed = true;
            }

            if (!removed) continue;
            RebuildSnapshot();
            if (_scanSessionActive)
                continue;
            ClientsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static List<ITransport> BuildSecondaryPresenceTransports(ITransport? bluetoothTransport,
        IEnumerable<ITransport>? additionalDiscoveryTransports)
    {
        var list = new List<ITransport>();
        if (bluetoothTransport != null && bluetoothTransport.Kind != TransportKind.Udp)
            list.Add(bluetoothTransport);
        if (additionalDiscoveryTransports == null)
            return list;

        foreach (var transport in additionalDiscoveryTransports)
        {
            if (transport == null || transport.Kind == TransportKind.Udp)
                continue;
            if (list.Contains(transport))
                continue;
            list.Add(transport);
        }

        return list;
    }
}