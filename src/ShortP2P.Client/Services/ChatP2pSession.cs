using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShortP2P.Auth;
using ShortP2P.Auth.Data;
using ShortP2P.Client.ChatMedia;
using ShortP2P.Client.ChatMedia.Vocabulary;
using ShortP2P.Client.Data;
using ShortP2P.Client.Routing;
using ShortP2P.Client.Transceivers;
using ShortP2P.Client.Transport;
using ShortP2P.Crypto;
using ShortP2P.Discovery;
using ShortP2P.Discovery.Profile;
using ShortP2P.Discovery.Transceivers;
using ShortP2P.Messenger;
using ShortP2P.Client.Services.MessengerServers;
using ShortP2P.Transport;
using ShortP2P.Transport.Abstractions;

namespace ShortP2P.Client.Services;

/// <summary>
///     Чат: UDP/Bluetooth, RSA-handshake (0x01) только у пира с меньшим network id (сравнение Guid);
///     второй шлёт запрос 0x04+16 байт id и ждёт handshake. Шифрокадры 0x02.
///     Leader и follower обмениваются сообщениями; BLE ConnectGatt — только у узла с большим MAC
///     (ответы — через GATT notify на уже открытой сессии, см. Bluetooth-транспорт).
/// </summary>
public sealed class ChatP2PSession : IAsyncDisposable
{
    private const byte FrameHandshake = 0x01;
    private const byte FrameCipher = 0x02;

    /// <summary>Запрос на установку сессии: только от пира с большим NetworkId; тело — 12 байт wire id отправителя.</summary>
    private const byte FrameSessionSetupRequest = 0x04;

    public const int MaxMessageChars = 32768;

    /// <summary>SHO-10: how many newest unsent outgoing rows to retry after UDP falls back.</summary>
    public const int UdpFallbackLatestUnsentCount = 20;
    private static readonly TimeSpan DecryptRecoveryCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InviteResyncCooldown = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _cryptoProbeLoopLock = new(1, 1);
    private readonly P2pCryptoSessionCache _cryptoSessionCache;
    private readonly SemaphoreSlim _flushPendingSem = new(1, 1);

    private readonly GuaranteedDeliveryPolicy _guaranteedDelivery = new();
    private readonly ILogger<ChatP2PSession> _logger;
    private readonly ChatMediaOptions _media;
    private readonly List<int> _pendingOutgoing = [];

    private readonly Lock _pendingSync = new();
    private readonly UserP2pRuntime _runtime;
    private readonly SemaphoreSlim _sessionSetup = new(1, 1);
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private readonly Lock _sync = new();
    private readonly TcpTransferService _tcpTransfer = new();
    private readonly AuthService _auth;
    private readonly ChatEntity _chat;
    private readonly LocalNetworkScanner? _localNetworkScanner;
    private readonly ChatRepository _repo;
    private readonly P2pRoutingSettings? _routingSettings;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private readonly UserEntity _user;
    private TaskCompletionSource<bool>? _cryptoProbeOkAwaiter;
    private volatile bool _cryptoProbeRoundTripOk;
    private volatile bool _userInfoSharedAfterEstablish;
    private string? _outstandingTestSendHash;
    private CancellationTokenSource? _cts;
    private int _decryptRecoveryGate;
    private int _inviteResyncGate;
    private DateTimeOffset _lastInviteResyncUtc = DateTimeOffset.MinValue;
    private TaskCompletionSource<bool>? _followerHandshakeTcs;

    /// <summary>True только у лидера (меньший NetworkId): отправлен свой RSA-handshake; test_send только с лидера.</summary>
    private bool _handshakeWeInitiated;

    /// <summary>Leader: last TX 0x01 frame (129 bytes). Resent on duplicate 0x04 without rotating AES keys.</summary>
    private byte[]? _lastLeaderHandshakeFrame;

    /// <summary>Follower: last accepted 0x01 payload (128 bytes) so identical LoRa resends don't rebuild the session.</summary>
    private byte[]? _lastAcceptedHandshakePayload;

    /// <summary>Inbound cipher held while local messenger/session is not ready (LoRa chunk trains often outrun handshake).</summary>
    private byte[]? _pendingInboundCipher;
    private TransportAddress? _pendingInboundCipherFrom;

    private DateTimeOffset _lastLeaderHandshakeSentUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan LeaderHandshakeResendMinInterval = TimeSpan.FromSeconds(15);

    private DateTimeOffset _lastDecryptRecoveryUtc = DateTimeOffset.MinValue;
    private MessengerService? _messenger;
    private CancellationTokenSource? _outboundCts;

    private TransportAddress? _peerAddress;
    private List<TransportAddress> _peerEndpoints = [];
    private RsaPublicKey? _peerPublicKey;
    private volatile bool _presenceHooked;
    private bool _startFinished;
    private bool _startInProgress;
    private bool _transceiverSubscribed;

    /// <summary>SHO-10: local UDP establish/send failed; skip UDP outbound until the session is recreated.</summary>
    private bool _udpOutboundUnavailable;
    private string? _activeMessengerServerBaseUrl;

    private int _udpFallbackApplied;

    /// <summary>Manual server/mesh choice. Auto still allows the UDP-failure workaround.</summary>
    private ChatDeliveryPath _deliveryPath = ChatDeliveryPath.Auto;
    private ChatHandshakeStatus _handshakeStatus = ChatHandshakeStatus.Idle;

    /// <summary>SMT-3: negotiated vocab map for this chat session (lang → version+hash or UNCOMPRESSED).</summary>
    private Dictionary<string, VocabAcceptEntry>? _vocabAccepted;

    private ChatP2PSession(
        ChatEntity chat,
        UserEntity user,
        AuthService auth,
        ChatRepository repo,
        UserP2pRuntime runtime,
        SynchronizationContext? uiSynchronizationContext = null,
        P2pRoutingSettings? routingSettings = null,
        LocalNetworkScanner? localNetworkScanner = null,
        ChatMediaOptions? chatMediaOptions = null,
        P2pCryptoSessionCache? cryptoSessionCache = null,
        ILogger<ChatP2PSession>? logger = null)
    {
        _chat = chat;
        _user = user;
        _auth = auth;
        _repo = repo;
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _uiSynchronizationContext = uiSynchronizationContext;
        _routingSettings = routingSettings;
        _localNetworkScanner = localNetworkScanner;
        _cryptoSessionCache = cryptoSessionCache ?? new P2pCryptoSessionCache();
        _media = chatMediaOptions ?? new ChatMediaOptions();
        _logger = logger ?? NullLogger<ChatP2PSession>.Instance;
    }

    private ITransport? BluetoothTransport => _runtime.BluetoothTransport;

    private TrafficQualityMode CurrentTrafficQuality =>
        _routingSettings?.TrafficQuality ?? TrafficQualityMode.Normal;

    public ValueTask DisposeAsync()
    {
        return StopAsync();
    }

    public static ChatP2PSession Create(
        ChatEntity chat,
        UserEntity user,
        AuthService auth,
        ChatRepository repo,
        UserP2pRuntime runtime,
        SynchronizationContext? uiSynchronizationContext = null,
        P2pRoutingSettings? routingSettings = null,
        LocalNetworkScanner? localNetworkScanner = null,
        ChatMediaOptions? chatMediaOptions = null,
        P2pCryptoSessionCache? cryptoSessionCache = null,
        ILogger<ChatP2PSession>? logger = null)
    {
        return new ChatP2PSession(chat, user, auth, repo, runtime, uiSynchronizationContext, routingSettings,
            localNetworkScanner, chatMediaOptions, cryptoSessionCache, logger);
    }

    /// <summary>Меньший NetworkId — единственный, кто высылает RSA-handshake / test_send; больший — только 0x04 и test_response.</summary>
    private bool IsCryptoSessionLeader() =>
        CryptoSessionLeadership.IsLeader(_user.NetworkIdShort, _chat.PeerNetworkIdShort);

    private string SessionRoleLabel() => IsCryptoSessionLeader() ? "leader" : "follower";

    private string PeerSessionRoleLabel() => IsCryptoSessionLeader() ? "follower" : "leader";

    private (string LocalRole, string PeerRole) GetCryptoSessionRoles() =>
        (SessionRoleLabel(), PeerSessionRoleLabel());

    private bool TryGetLocalBluetoothMac(out byte[] mac)
    {
        mac = [];
        if (_routingSettings?.EnableBluetoothTransport == false)
            return false;
        var macText = _routingSettings?.SelectedBluetoothAdapterMac;
        return !string.IsNullOrWhiteSpace(macText)
               && BluetoothTransportAddress.TryParseMac(macText.Trim(), out mac);
    }

    private bool TryGetPeerBluetoothMac(out byte[] mac)
    {
        mac = [];
        foreach (var ep in _peerEndpoints)
        {
            if (ep.Kind != TransportKind.Bluetooth || ep.Data.Length != BluetoothTransportAddress.MacLength)
                continue;
            mac = ep.Data.ToArray();
            return true;
        }

        foreach (var token in PeerHostList.ParseEndpointCandidates(_chat.PeerHost))
        {
            if (!BluetoothTransportAddress.TryParseMac(token, out mac))
                continue;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     SHO-10: BLE fallback only when the transport is enabled, the adapter is up, and the peer has a MAC.
    /// </summary>
    public static bool ShouldTryBleFallback(bool bleEnabled, bool bleTransportAvailable, bool peerHasBleAddress) =>
        bleEnabled && bleTransportAvailable && peerHasBleAddress;

    public static string DeliveryPathLabel(ChatDeliveryPath path) => path switch
    {
        ChatDeliveryPath.Server => "Путь: сервер",
        ChatDeliveryPath.Mesh => "Путь: mesh",
        _ => "Путь: авто"
    };

    /// <summary>From auto or mesh the next manual choice is server; from server it is mesh.</summary>
    public static ChatDeliveryPath NextManualDeliveryPath(ChatDeliveryPath current) =>
        current == ChatDeliveryPath.Server ? ChatDeliveryPath.Mesh : ChatDeliveryPath.Server;

    public static bool AllowsAutomaticServerFallback(ChatDeliveryPath path) =>
        path == ChatDeliveryPath.Auto;

    /// <summary>Больший MAC — BLE leader (инициирует ConnectGatt); меньший — BLE follower (ждёт входящее).</summary>
    private bool IsBleConnectionLeader()
    {
        if (!TryGetLocalBluetoothMac(out var local) || !TryGetPeerBluetoothMac(out var peer))
            return false;
        return BluetoothTransportAddress.ShouldInitiateBleConnection(local, peer);
    }

    private string? BleSessionRoleLabel() =>
        TryGetLocalBluetoothMac(out _) && TryGetPeerBluetoothMac(out _)
            ? IsBleConnectionLeader() ? "leader" : "follower"
            : null;

    private string? PeerBleSessionRoleLabel()
    {
        var local = BleSessionRoleLabel();
        return local switch
        {
            "leader" => "follower",
            "follower" => "leader",
            _ => null
        };
    }

    private (string? LocalMac, string? PeerMac, string? LocalRole, string? PeerRole) GetBleSessionRoles()
    {
        if (!TryGetLocalBluetoothMac(out var localBytes) || !TryGetPeerBluetoothMac(out var peerBytes))
            return (null, null, null, null);

        var localMac = BluetoothTransportAddress.ToMacString(localBytes);
        var peerMac = BluetoothTransportAddress.ToMacString(peerBytes);
        var localRole = BluetoothTransportAddress.ShouldInitiateBleConnection(localBytes, peerBytes)
            ? "leader"
            : "follower";
        var peerRole = localRole == "leader" ? "follower" : "leader";
        return (localMac, peerMac, localRole, peerRole);
    }

    private void LogSessionRoleContext(string eventName, LogLevel level = LogLevel.Information)
    {
        var (cryptoLocal, cryptoPeer) = GetCryptoSessionRoles();
        var (bleLocalMac, blePeerMac, bleLocalRole, blePeerRole) = GetBleSessionRoles();
        if (bleLocalRole != null)
        {
            _logger.Log(level,
                "Chat {ChatId}: {Event} — crypto local={Role} peer={PeerRole}; BLE local={BleRole} (mac={LocalBleMac}) peer={BlePeerRole} (mac={PeerBleMac})",
                _chat.Id, eventName, cryptoLocal, cryptoPeer, bleLocalRole, bleLocalMac, blePeerRole, blePeerMac);
            return;
        }

        _logger.Log(level,
            "Chat {ChatId}: {Event} — crypto local={Role} peer={PeerRole}; BLE roles unavailable (mac unknown)",
            _chat.Id, eventName, cryptoLocal, cryptoPeer);
    }

    private static string FormatTransportAddress(TransportAddress address)
    {
        try
        {
            return address.Kind switch
            {
                TransportKind.Udp => UdpTransportAddress.ToIPEndPoint(address).ToString(),
                TransportKind.Bluetooth => BluetoothTransportAddress.ToMacString(address.Data),
                _ => $"{address.Kind}:{Convert.ToHexString(address.Data)}"
            };
        }
        catch
        {
            return $"{address.Kind}:{Convert.ToHexString(address.Data)}";
        }
    }

    private bool TryGetCryptoSession([NotNullWhen(true)] out P2PSession? session)
    {
        return _cryptoSessionCache.TryGetSession(_chat.Id, out session);
    }

    private void ClearCryptoSession()
    {
        _lastLeaderHandshakeFrame = null;
        _lastAcceptedHandshakePayload = null;
        if (_cryptoSessionCache.TryRemove(_chat.Id, out _))
            _logger.LogInformation("Chat {ChatId}: crypto session cleared from cache (role={Role})", _chat.Id,
                SessionRoleLabel());
    }

    /// <summary>Follower (больший NetworkId): сессия и messenger готовы к обмену cipher.</summary>
    private bool IsFollowerCryptoReady()
    {
        lock (_sync)
        {
            return IsFollowerCryptoReadyCore();
        }
    }

    private bool IsFollowerCryptoReadyCore()
    {
        return !IsCryptoSessionLeader() && TryGetCryptoSession(out _) && _messenger != null;
    }

    /// <summary>Успешное завершение ожидания handshake у follower (0x01 от лидера).</summary>
    private void SignalFollowerHandshakeSuccess(TaskCompletionSource<bool>? waiter = null)
    {
        CompleteFollowerHandshakeWait(true, waiter: waiter);
    }

    /// <summary>Ошибка ожидания (таймаут, сеть, сбой отправки 0x04).</summary>
    private void SignalFollowerHandshakeFailure(Exception ex, TaskCompletionSource<bool>? waiter = null)
    {
        CompleteFollowerHandshakeWait(false, ex, waiter);
    }

    /// <summary>Отмена ожидания (стоп чата, сброс крипты).</summary>
    private void CancelFollowerHandshakeWait()
    {
        CompleteFollowerHandshakeWait(false, null, null, true);
    }

    private void CompleteFollowerHandshakeWait(bool success, Exception? failure = null,
        TaskCompletionSource<bool>? waiter = null, bool canceled = false)
    {
        lock (_sync)
        {
            var tcs = waiter ?? _followerHandshakeTcs;
            if (tcs is not { Task.IsCompleted: false })
                return;

            if (success)
            {
                _logger.LogInformation("Chat {ChatId}: follower handshake wait completed", _chat.Id);
                tcs.TrySetResult(true);
            }
            else if (canceled)
            {
                _logger.LogDebug("Chat {ChatId}: follower handshake wait canceled", _chat.Id);
                tcs.TrySetCanceled();
            }
            else if (failure != null)
            {
                _logger.LogWarning(failure, "Chat {ChatId}: follower handshake wait failed", _chat.Id);
                tcs.TrySetException(failure);
            }

            if (ReferenceEquals(_followerHandshakeTcs, tcs))
                _followerHandshakeTcs = null;
        }
    }

    private async ValueTask<P2PSession> WaitForCryptoSessionAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (TryGetCryptoSession(out var session))
                return session;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    public event EventHandler? MessagesChanged;
    public event EventHandler<int>? TransferStateChanged;
    public event EventHandler? HandshakeStatusChanged;

    /// <summary>
    /// UI refresh when delivery status changes outside this session (e.g. server delivery receipt).
    /// </summary>
    public void NotifyMessagesChangedFromExternal() => RaiseMessagesChanged();

    /// <summary>Текущий статус handshake для шапки чата.</summary>
    public ChatHandshakeStatus HandshakeStatus => _handshakeStatus;

    private void RaiseMessagesChanged()
    {
        if (_uiSynchronizationContext != null)
            _uiSynchronizationContext.Post(_ => MessagesChanged?.Invoke(this, EventArgs.Empty), null);
        else
            MessagesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseHandshakeStatusChanged()
    {
        if (_uiSynchronizationContext != null)
            _uiSynchronizationContext.Post(_ => HandshakeStatusChanged?.Invoke(this, EventArgs.Empty), null);
        else
            HandshakeStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetHandshakeStatus(ChatHandshakeStatus status)
    {
        if (_handshakeStatus == status)
            return;
        _handshakeStatus = status;
        RaiseHandshakeStatusChanged();
    }

    private void RefreshHandshakeStatus()
    {
        // UI "сессия установлена" only after a matching test_response confirms the crypto path.
        if (_cryptoProbeRoundTripOk)
        {
            SetHandshakeStatus(ChatHandshakeStatus.Established);
            return;
        }

        if (_startInProgress || _startFinished || _handshakeWeInitiated ||
            TryGetCryptoSession(out _) || _messenger != null)
        {
            SetHandshakeStatus(ChatHandshakeStatus.InProgress);
            return;
        }

        SetHandshakeStatus(ChatHandshakeStatus.Idle);
    }

    private async Task RaiseMessagesChangedIfVisibleAsync()
    {
        if (await _repo.IsChatFromBlockedPeerAsync(_chat.Id).ConfigureAwait(false))
            return;
        RaiseMessagesChanged();
    }

    private void RebuildRouteFromChat()
    {
        try
        {
            _peerPublicKey = string.IsNullOrWhiteSpace(_chat.PeerRsaPublicJson)
                ? null
                : RsaKeySerializer.DeserializePublic(_chat.PeerRsaPublicJson);
        }
        catch (Exception ex)
        {
            _peerPublicKey = null;
            _logger.LogWarning(ex, "Chat {ChatId}: peer public key is missing or invalid", _chat.Id);
        }

        _peerEndpoints = PeerTransportEndpoints.Parse(_chat).ToList();
        if (_peerEndpoints.Count == 0)
        {
            var primary = PeerHostList.PrimaryHost(_chat.PeerHost);
            if (IPAddress.TryParse(primary, out var ip))
                _peerEndpoints.Add(UdpTransportAddress.FromIPEndPoint(new IPEndPoint(ip, _chat.PeerPort)));
            else if (BluetoothTransportAddress.TryParseMac(primary, out var mac))
                _peerEndpoints.Add(BluetoothTransportAddress.FromMac(mac));
            else if (!string.IsNullOrWhiteSpace(primary) &&
                     !CompressedNetworkId.TryParseShortString(primary, out _))
                _logger.LogWarning(
                    "Chat {ChatId}: unsupported peer host format '{PeerHost}' — leaving endpoints empty",
                    _chat.Id, primary);
        }

        _peerAddress = _peerEndpoints.Count > 0 ? _peerEndpoints[0] : null;
        _logger.LogDebug(
            "Chat {ChatId}: route rebuilt, peer endpoints={EndpointCount}, primary={PrimaryEndpoint}, relay={HasRelay}",
            _chat.Id,
            _peerEndpoints.Count,
            _peerAddress != null ? FormatTransportAddress(_peerAddress) : "(none)",
            !string.IsNullOrEmpty(_chat.RelayRouteBlob));
        foreach (var ep in _peerEndpoints)
            if (_localNetworkScanner != null)
            {
                if (ep.Kind == TransportKind.Bluetooth)
                    _localNetworkScanner.RememberBluetoothPeer(ep);
                else if (ep.Kind == TransportKind.Udp)
                    try
                    {
                        var ip = UdpTransportAddress.ToIPEndPoint(ep).Address.ToString();
                        _localNetworkScanner.RememberUdpPresenceTarget(ip);
                    }
                    catch
                    {
                        // invalid UDP endpoint payload in storage
                    }
            }
    }

    /// <summary>Обновляет строку чата из БД (тот же Id), не создавая новую сессию.</summary>
    public void ApplyChatRow(ChatEntity row)
    {
        if (row.Id != _chat.Id)
            throw new ArgumentException("Chat id mismatch.", nameof(row));
        var previousKey = _chat.PeerRsaPublicJson;
        _chat.PeerNickname = row.PeerNickname;
        _chat.PeerNetworkIdShort = row.PeerNetworkIdShort;
        _chat.PeerRsaPublicJson = row.PeerRsaPublicJson;
        _chat.PeerHost = row.PeerHost;
        _chat.PeerPort = row.PeerPort;
        _chat.PeerEndpointsJson = row.PeerEndpointsJson;
        _chat.PeerKeySourceKind = row.PeerKeySourceKind;
        _chat.PeerKeySourceDetail = row.PeerKeySourceDetail;
        _chat.RelayRouteBlob = row.RelayRouteBlob;
        _chat.DeliveryPath = row.DeliveryPath;
        _chat.IsBot = row.IsBot;
        _chat.BotCatalogChecked = row.BotCatalogChecked;
        _chat.UpdatedUtcTicks = row.UpdatedUtcTicks;
        AdoptStoredDeliveryPath();
        _logger.LogInformation(
            "Chat {ChatId}: chat row applied (peer={PeerNetworkId}, host={PeerHost}, port={PeerPort})",
            _chat.Id, _chat.PeerNetworkIdShort, _chat.PeerHost, _chat.PeerPort);
        RebuildRouteFromChat();
        if (!string.IsNullOrWhiteSpace(previousKey) &&
            !SafetyNumber.PublicKeyJsonEquals(previousKey, row.PeerRsaPublicJson))
        {
            _logger.LogWarning("Chat {ChatId}: peer public key changed, clearing crypto session", _chat.Id);
            ClearCryptoSession();
        }
    }

    private bool ShouldAcceptIncomingFrom(TransportAddress from)
    {
        if (!IsTransportEnabled(from.Kind))
            return false;
        if (_peerEndpoints.Any(x => x.Kind == from.Kind && x.Data.AsSpan().SequenceEqual(from.Data)))
            return true;
        try
        {
            if (from.Kind == TransportKind.Udp)
            {
                var ep = UdpTransportAddress.ToIPEndPoint(from);
                foreach (var h in PeerHostList.ParseIpCandidates(_chat.PeerHost))
                {
                    if (!IPAddress.TryParse(h, out var ip))
                        continue;
                    if (ep.Address.Equals(ip))
                        return true;
                }
            }

            if (from.Kind == TransportKind.Bluetooth) return true;

            // Host transports (LoRa/Meshtastic): only frames from THIS chat's peer MeshId.
            // A blanket accept made every open chat consume the same 0x01 and create bogus sessions.
            if (from.Kind is not (TransportKind.Udp or TransportKind.Bluetooth or TransportKind.MessengerServer))
                return IsHostTransportAddressForThisPeer(from);

            if (!string.IsNullOrEmpty(_chat.RelayRouteBlob))
                return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>
    ///     True when the host transport maps <paramref name="from"/> to this chat's peer NetworkId
    ///     (MeshDirectory / recent presence). Pulls fresh addresses before comparing.
    /// </summary>
    private bool IsHostTransportAddressForThisPeer(TransportAddress from)
    {
        MergeAdditionalTransportPeerAddresses();
        if (_peerEndpoints.Any(x => x.Kind == from.Kind && x.Data.AsSpan().SequenceEqual(from.Data)))
            return true;

        var peerShort = _chat.PeerNetworkIdShort?.Trim();
        var provider = _runtime.AdditionalTransports;
        if (provider == null || string.IsNullOrWhiteSpace(peerShort))
            return false;

        try
        {
            foreach (var ep in provider.GetPeerAddresses(peerShort))
            {
                if (ep.Kind == from.Kind && ep.Data.AsSpan().SequenceEqual(from.Data))
                    return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Chat {ChatId}: host-transport peer address check failed", _chat.Id);
        }

        return false;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_startFinished)
            return;

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_startFinished)
                return;

            RebuildRouteFromChat();
            AdoptStoredDeliveryPath();
            // Pull LoRa/mesh addresses early so leader 0x01 and follower 0x04 can leave on radio
            // even when UDP still looks locally "up".
            MergeAdditionalTransportPeerAddresses();
            LogSessionRoleContext("P2P session starting");
            _startInProgress = true;
            RefreshHandshakeStatus();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ResetOutboundCts();

            if (_chat.IsBot)
            {
                // Bots use BotClientInteraction only — no ChatRequest, UDP invite, or mesh crypto.
                _deliveryPath = ChatDeliveryPath.Server;
                _udpFallbackApplied = 1;
                _udpOutboundUnavailable = true;
                if (PeerKeySource.IsServer(_chat.PeerKeySourceKind))
                    _activeMessengerServerBaseUrl = _chat.PeerKeySourceDetail;
                RaiseActiveChannelChanged();
                _startInProgress = false;
                _startFinished = true;
                RefreshHandshakeStatus();
                LogSessionRoleContext("Bot session ready (server-only)");
                return;
            }

            SubscribeToTransceivers();

            // Only the crypto leader publishes invites / ChatRequests and drives test_send.
            // Follower waits for invite/handshake and replies with test_response.
            var isLeader = IsCryptoSessionLeader();

            if (isLeader)
            {
                try
                {
                    var servers = _runtime.MessengerServers;
                    if (servers != null)
                    {
                        await servers.PublishChatRequestAsync(_chat.PeerNetworkIdShort, cancellationToken)
                            .ConfigureAwait(false);
                        if (_deliveryPath != ChatDeliveryPath.Mesh)
                            await TryServerFallbackAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Chat {ChatId}: server ChatRequest publish failed during session start",
                        _chat.Id);
                }
            }

            var udpUnavailable = false;
            if (isLeader)
            {
                try
                {
                    if (_deliveryPath != ChatDeliveryPath.Server)
                        await SendChatInviteWithRetryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Chat {ChatId}: invite send failed during session start", _chat.Id);
                    if (IsUdpUnavailableError(ex))
                        _udpOutboundUnavailable = true;
                }
            }

            udpUnavailable = IsUdpConnectionUnavailable();
            if (_deliveryPath == ChatDeliveryPath.Server)
            {
                _udpFallbackApplied = 1;
                _udpOutboundUnavailable = true;
                udpUnavailable = true;
                if (isLeader)
                    await TryServerFallbackAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (_deliveryPath == ChatDeliveryPath.Mesh)
            {
                _udpFallbackApplied = 1;
                // Prefer LoRa/mesh endpoints so 0x04/0x01 leave on radio when UDP is down.
                MergeAdditionalTransportPeerAddresses();
                PreferAdditionalTransportEndpoint();
            }
            else if (udpUnavailable)
            {
                _logger.LogWarning(
                    "Chat {ChatId}: error while establishing connection by UDP; trying BLE, host transports (LoRa), and messenger server",
                    _chat.Id);
                await TryApplyUdpUnavailableWorkaroundAsync(cancellationToken, flushUnsent: false)
                    .ConfigureAwait(false);
            }

            try
            {
                await EnsureSessionAsInitiatorAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Chat {ChatId}: session setup failed during session start", _chat.Id);
                if (IsUdpUnavailableError(ex))
                    _udpOutboundUnavailable = true;
            }

            if (_deliveryPath == ChatDeliveryPath.Auto && !udpUnavailable && IsUdpConnectionUnavailable())
            {
                _logger.LogWarning(
                    "Chat {ChatId}: error while establishing connection by UDP; trying BLE, host transports (LoRa), and messenger server",
                    _chat.Id);
                await TryApplyUdpUnavailableWorkaroundAsync(cancellationToken, flushUnsent: false)
                    .ConfigureAwait(false);
                udpUnavailable = true;
            }

            if (isLeader)
                _ = TryConfirmCryptoSessionAsync(cancellationToken);
            else
            {
                // LoRa is lossy and the leader may come up later — nudge a few times, then stop.
                _ = NudgeLeaderSessionSetupAsync(_cts?.Token ?? cancellationToken);
            }

            HookPresenceForPendingFlush();
            _startFinished = true;
            _startInProgress = false;
            RefreshHandshakeStatus();
            if (udpUnavailable)
                await EnqueueLatestUnsentMessagesAsync(cancellationToken).ConfigureAwait(false);
            LogSessionRoleContext("P2P session start completed");
        }
        finally
        {
            _startInProgress = false;
            RefreshHandshakeStatus();
            _startGate.Release();
        }
    }

    /// <summary>Inbox с messenger-сервера принимается только после handshake.</summary>
    public bool IsReadyForServerReceive => _startFinished;

    /// <summary>False while the user has forced mesh: server inbox for this chat is ignored.</summary>
    public bool AcceptsServerTransport => _deliveryPath != ChatDeliveryPath.Mesh;

    public ChatDeliveryPath DeliveryPath => _deliveryPath;

    /// <summary>
    ///     True when send/receive for this chat is pinned to a messenger server (manual Server path,
    ///     Auto server-first pin after hosting-server discovery, or bot chats).
    /// </summary>
    public bool IsCommunicatingViaMessengerServer
    {
        get
        {
            if (_deliveryPath == ChatDeliveryPath.Mesh)
                return false;
            if (_deliveryPath == ChatDeliveryPath.Server || _chat.IsBot)
                return true;
            if (!string.IsNullOrWhiteSpace(_activeMessengerServerBaseUrl))
                return true;
            return _runtime.MessengerServers?.TryGetPreferredServerBaseUrl(_chat.PeerNetworkIdShort, out _) == true;
        }
    }

    /// <summary>Base URL of the active messenger-server pin, when known.</summary>
    public string? ActiveMessengerServerBaseUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_activeMessengerServerBaseUrl))
                return _activeMessengerServerBaseUrl;
            if (_runtime.MessengerServers != null &&
                _runtime.MessengerServers.TryGetPreferredServerBaseUrl(_chat.PeerNetworkIdShort, out var preferred) &&
                !string.IsNullOrWhiteSpace(preferred))
                return preferred;
            if (PeerKeySource.IsServer(_chat.PeerKeySourceKind))
                return string.IsNullOrWhiteSpace(_chat.PeerKeySourceDetail) ? null : _chat.PeerKeySourceDetail;
            return null;
        }
    }

    public event EventHandler? DeliveryPathChanged;

    /// <summary>Raised when the effective transport channel for the safety UI may have changed.</summary>
    public event EventHandler? ActiveChannelChanged;

    private void SubscribeToTransceivers()
    {
        if (_transceiverSubscribed)
            return;
        var handshake = _runtime.Handshake;
        var message = _runtime.Message;
        if (handshake == null && message == null)
            return;

        if (handshake != null)
            handshake.GotData += OnHandshakeReceived;

        if (message != null)
            message.GotData += OnCipherReceived;

        // UDP invites are handled solely by UserP2pRuntime → HandlePeerInviteResyncAsync
        // (subscribing here too caused a dual-handler resync storm).

        _transceiverSubscribed = true;
    }

    private void UnsubscribeFromTransceivers()
    {
        if (!_transceiverSubscribed)
            return;
        var handshake = _runtime.Handshake;
        if (handshake != null)
            handshake.GotData -= OnHandshakeReceived;

        var message = _runtime.Message;
        if (message != null)
            message.GotData -= OnCipherReceived;

        _transceiverSubscribed = false;
    }

    private void OnHandshakeReceived(object? sender, HandshakeMessage msg)
    {
        if (!ShouldAcceptIncomingFrom(msg.RemoteAddress))
        {
            _logger.LogDebug(
                "Chat {ChatId}: ignored handshake frame {Kind} from {Remote}",
                _chat.Id, msg.Kind, FormatTransportAddress(msg.RemoteAddress));
            return;
        }

        _logger.LogDebug(
            "Chat {ChatId}: received handshake frame {Kind} from {Remote}",
            _chat.Id, msg.Kind, FormatTransportAddress(msg.RemoteAddress));
        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => HandleHandshakeAsync(msg, token), token);
    }

    private async Task HandleHandshakeAsync(HandshakeMessage msg, CancellationToken cancellationToken)
    {
        try
        {
            switch (msg.Kind)
            {
                case HandshakeKind.Handshake:
                    _logger.LogInformation(
                        "Chat {ChatId}: processing RSA handshake (0x01) from {Remote}",
                        _chat.Id, FormatTransportAddress(msg.RemoteAddress));
                    await ProcessHandshakePacketAsync(msg.Body, msg.RemoteAddress, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                case HandshakeKind.SessionSetupRequest:
                    _logger.LogInformation(
                        "Chat {ChatId}: processing session setup request (0x04) from {Remote}",
                        _chat.Id, FormatTransportAddress(msg.RemoteAddress));
                    await ProcessSessionSetupRequestAsync(msg.Body, msg.RemoteAddress, cancellationToken)
                        .ConfigureAwait(false);
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Chat {ChatId}: handshake handling canceled ({Kind})", _chat.Id, msg.Kind);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: handshake handling failed ({Kind})", _chat.Id, msg.Kind);
        }
    }

    private void OnCipherReceived(object? sender, TransportReceiveMessage msg)
    {
        if (!ShouldAcceptIncomingFrom(msg.RemoteAddress))
        {
            _logger.LogInformation(
                "Chat {ChatId}: ignored inbound cipher from {Remote} (endpoint not accepted)",
                _chat.Id, FormatTransportAddress(msg.RemoteAddress));
            return;
        }

        _logger.LogInformation(
            "Chat {ChatId}: RX cipher {Bytes} bytes from {Remote}",
            _chat.Id, msg.Payload.Length, FormatTransportAddress(msg.RemoteAddress));
        TryRememberAdditionalPeer(msg.RemoteAddress);
        MessengerService? messenger;
        lock (_sync)
        {
            messenger = _messenger;
            if (messenger == null || !TryGetCryptoSession(out _))
            {
                _pendingInboundCipher = msg.Payload.ToArray();
                _pendingInboundCipherFrom = msg.RemoteAddress;
                _logger.LogWarning(
                    "Chat {ChatId}: buffered inbound cipher ({Bytes} bytes) — messenger/crypto session not ready; nudging handshake",
                    _chat.Id, msg.Payload.Length);
                messenger = null;
            }
        }

        if (messenger == null)
        {
            NudgeCryptoAfterInboundWithoutSession();
            return;
        }

        messenger.TryAcceptCipher(msg);
        TryFlushPendingInboundCipher();
    }

    /// <summary>
    ///     Cipher arrived before local AES keys: buffer it, follower re-asks 0x04, leader re-drives 0x01.
    ///     Common on LoRa when handshake chunks timed out but a later cipher chunk train completed.
    /// </summary>
    private void NudgeCryptoAfterInboundWithoutSession()
    {
        MergeAdditionalTransportPeerAddresses();
        PreferAdditionalTransportEndpoint();
        var token = _cts?.Token ?? CancellationToken.None;
        if (IsCryptoSessionLeader())
            _ = EnsureLeaderAfterInboundCipherAsync(token);
        else
            _ = SendSessionSetupRequestQuietAsync(token);
    }

    private async Task EnsureLeaderAfterInboundCipherAsync(CancellationToken cancellationToken)
    {
        try
        {
            // After probe ResetCryptoState, TryConfirm alone is a no-op (_handshakeWeInitiated=false, messenger=null).
            await EnsureSessionAsInitiatorAsync(cancellationToken).ConfigureAwait(false);
            TryFlushPendingInboundCipher();
            await TryConfirmCryptoSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Chat {ChatId}: leader session nudge after inbound cipher failed", _chat.Id);
        }
    }

    private void TryFlushPendingInboundCipher()
    {
        byte[]? pending;
        TransportAddress? from;
        MessengerService? messenger;
        lock (_sync)
        {
            pending = _pendingInboundCipher;
            from = _pendingInboundCipherFrom;
            messenger = _messenger;
            if (pending == null || from == null || messenger == null || !TryGetCryptoSession(out _))
                return;
            _pendingInboundCipher = null;
            _pendingInboundCipherFrom = null;
        }

        _logger.LogInformation(
            "Chat {ChatId}: flushing buffered inbound cipher ({Len} bytes)",
            _chat.Id, pending.Length);
        messenger.TryAcceptCipher(new TransportReceiveMessage(pending, from));
    }

    private async Task SendSessionSetupRequestQuietAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SendSessionSetupRequestPacketAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Chat {ChatId}: session setup nudge after inbound cipher failed", _chat.Id);
        }
    }

    /// <summary>
    ///     Host mesh directory (e.g. LoRa): bind peer NetworkId to the radio address after cipher attribution.
    /// </summary>
    private void TryRememberAdditionalPeer(TransportAddress from)
    {
        if (from.Kind is TransportKind.Udp or TransportKind.Bluetooth)
            return;
        var provider = _runtime.AdditionalTransports;
        if (provider == null || !provider.IsEnabled(from.Kind))
            return;
        try
        {
            var wire = CompressedNetworkId.FromShortString(_chat.PeerNetworkIdShort).ToWireBytes();
            provider.RememberPeer(wire, from);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Chat {ChatId}: additional transport RememberPeer failed", _chat.Id);
        }
    }

    /// <summary>Ingest a decrypted chat wire delivered via messenger server (servers-first path).</summary>
    public async Task IngestIncomingWireFromServerAsync(
        byte[] payload,
        CancellationToken cancellationToken = default,
        string? blobServerBaseUrl = null)
    {
        Require.NotNull(payload);
        if (await ProcessIncomingPayloadAsync(payload, cancellationToken, blobServerBaseUrl).ConfigureAwait(false))
            await RaiseMessagesChangedIfVisibleAsync().ConfigureAwait(false);
    }

    private async void OnMessengerGotData(object? sender, IncomingBinaryMessage incoming)
    {
        try
        {
            var payload = incoming.Payload.ToArray();
            var cancellationToken = _cts?.Token ?? CancellationToken.None;
            if (await ProcessIncomingPayloadAsync(payload, cancellationToken).ConfigureAwait(false))
                await RaiseMessagesChangedIfVisibleAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore to avoid breaking messenger callbacks
        }
    }

    /// <returns>True if UI should refresh the message list.</returns>
    private async Task<bool> ProcessIncomingPayloadAsync(
        byte[] payload,
        CancellationToken cancellationToken,
        string? blobServerBaseUrl = null)
    {
        var shouldNotify = false;
        if (ChatWireCodec.TryParse(payload, out var wire) && wire != null)
        {
            switch (wire)
            {
                case ChatWireText t:
                    if (SessionCryptoProbe.LooksLikeTestMessage(t.Text))
                    {
                        if (!TryHandleSessionCryptoProbeText(t.Text, cancellationToken))
                            _logger.LogWarning(
                                "Chat {ChatId}: ignored malformed/unmatched session test frame (not shown in UI)",
                                _chat.Id);
                    }
                    else
                    {
                        _ = await _repo.AddMessageAsync(_chat.Id, false, t.Text).ConfigureAwait(false);
                        shouldNotify = true;
                    }

                    break;
                case ChatWireImage img:
                    _ = await _repo.AddImageMessageAsync(_chat.Id, false, img.MimeType, img.ImageBytes)
                        .ConfigureAwait(false);
                    shouldNotify = true;
                    break;
                case ChatWireFile f:
                    if (_chat.IsBot)
                    {
                        _ = await _repo.AddFileMessageAsync(_chat.Id, false, f.FileName, f.MimeType, f.FileBytes)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        try
                        {
                            _media.ValidateDocumentMime(f.MimeType);
                            _media.ValidateFileSize(f.MimeType, f.FileBytes.Length, CurrentTrafficQuality);
                            _ = await _repo.AddFileMessageAsync(_chat.Id, false, f.FileName, f.MimeType, f.FileBytes)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            _ = await _repo.AddMessageAsync(_chat.Id, false,
                                    "[Входящий файл отклонён: неподдерживаемый тип или размер.]")
                                .ConfigureAwait(false);
                        }
                    }

                    shouldNotify = true;
                    break;
                case ChatWireTransferOffer offer:
                    await HandleTransferOfferAsync(offer, blobServerBaseUrl).ConfigureAwait(false);
                    shouldNotify = true;
                    break;
                case ChatWireTransferControl control:
                    await HandleTransferControlAsync(control, cancellationToken).ConfigureAwait(false);
                    shouldNotify = true;
                    break;
                case ChatWireVocabOffer offer:
                    await HandleVocabOfferAsync(offer, cancellationToken).ConfigureAwait(false);
                    break;
                case ChatWireVocabAccept accept:
                    HandleVocabAccept(accept);
                    break;
                case ChatWireCompressedText compressed:
                    if (TryDecodeCompressedText(compressed, out var decodedText))
                    {
                        if (SessionCryptoProbe.LooksLikeTestMessage(decodedText))
                        {
                            if (!TryHandleSessionCryptoProbeText(decodedText, cancellationToken))
                                _logger.LogWarning(
                                    "Chat {ChatId}: ignored malformed/unmatched session test frame (compressed)",
                                    _chat.Id);
                        }
                        else
                        {
                            _ = await _repo.AddMessageAsync(_chat.Id, false, decodedText).ConfigureAwait(false);
                            shouldNotify = true;
                        }
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Chat {ChatId}: failed to decode compressed text ({Lang} v{Ver})",
                            _chat.Id, compressed.Language, compressed.DictVersion);
                    }

                    break;
                case ChatWireUserInfo userInfo:
                    await ApplyIncomingUserInfoAsync(userInfo, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        else if (ChatWireCodec.LooksLikeFramedWire(payload))
        {
            _ = await _repo.AddMessageAsync(_chat.Id, false,
                    "[Входящее сообщение не распознано. Обновите клиент.]")
                .ConfigureAwait(false);
            shouldNotify = true;
        }
        else
        {
            var text = Encoding.UTF8.GetString(payload);
            if (SessionCryptoProbe.LooksLikeTestMessage(text))
            {
                if (!TryHandleSessionCryptoProbeText(text, cancellationToken))
                    _logger.LogWarning(
                        "Chat {ChatId}: ignored malformed/unmatched session test frame (not shown in UI)",
                        _chat.Id);
            }
            else
            {
                _ = await _repo.AddMessageAsync(_chat.Id, false, text).ConfigureAwait(false);
                shouldNotify = true;
            }
        }

        return shouldNotify;
    }

    /// <summary>
    ///     Peer (re)invite / ChatRequest arrived: reload route/key from DB and drop AES state.
    ///     Only the crypto leader re-initiates handshake and the test_send verification loop;
    ///     the follower accepts the reset and waits for leader handshake / test_send.
    /// </summary>
    public async Task HandlePeerInviteResyncAsync(CancellationToken cancellationToken = default)
    {
        if (_cts == null)
            return;
        if (Interlocked.CompareExchange(ref _inviteResyncGate, 1, 0) != 0)
            return;

        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastInviteResyncUtc < InviteResyncCooldown)
                return;
            _lastInviteResyncUtc = now;

            using var linked = _cts is { IsCancellationRequested: false } sessionCts
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionCts.Token)
                : null;
            var token = linked?.Token ?? cancellationToken;

            var isLeader = IsCryptoSessionLeader();
            _logger.LogInformation(
                "Chat {ChatId}: peer invite — resetting crypto (role={Role}, drive={Drive})",
                _chat.Id, SessionRoleLabel(), isLeader);
            SetHandshakeStatus(ChatHandshakeStatus.InProgress);

            var fresh = await _repo.GetChatAsync(_chat.Id).ConfigureAwait(false);
            if (fresh != null)
                ApplyChatRow(fresh);

            await ResetCryptoStateAsync(token).ConfigureAwait(false);

            if (isLeader)
            {
                await EnsureSessionAsInitiatorAsync(token).ConfigureAwait(false);
                _ = TryConfirmCryptoSessionAsync(token);
            }
            else
            {
                // Follower: accept new session; nudge leader once; do not start probe retries.
                try
                {
                    await SendSessionSetupRequestPacketAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Chat {ChatId}: follower 0x04 after invite resync failed", _chat.Id);
                }

                RefreshHandshakeStatus();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Chat {ChatId}: peer-invite resync canceled", _chat.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: peer-invite resync failed", _chat.Id);
        }
        finally
        {
            Interlocked.Exchange(ref _inviteResyncGate, 0);
        }
    }

    /// <summary>
    ///     Сохраняет новый IP/порт пира (прямой UDP), убирает цепочку ретрансляции и сбрасывает шифросессию.
    /// </summary>
    public async Task ApplyPeerEndpointAsync(string peerHost, int peerPort,
        CancellationToken cancellationToken = default)
    {
        Require.NotNullOrWhiteSpace(peerHost);
        peerHost = peerHost.Trim();
        _ = IPAddress.Parse(peerHost);
        if (peerPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(peerPort));

        var mergedHost = PeerHostList.WithPrimaryFirst(_chat.PeerHost, peerHost);
        await _repo.UpdateChatP2pRouteAsync(_chat.Id, mergedHost, peerPort, null).ConfigureAwait(false);
        _chat.PeerHost = mergedHost;
        _chat.PeerPort = peerPort;
        _chat.RelayRouteBlob = null;
        RebuildRouteFromChat();
        await ResetCryptoStateAsync(cancellationToken).ConfigureAwait(false);
        if (IsCryptoSessionLeader())
        {
            try
            {
                await SendChatInviteWithRetryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            try
            {
                await EnsureSessionAsInitiatorAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            _ = TryConfirmCryptoSessionAsync(cancellationToken);
        }
        else
        {
            try
            {
                await SendSessionSetupRequestPacketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>Временная отладка UI: сброс AES и повторная установка сессии по правилам лидера/подписчика.</summary>
    public async Task TechSendHandshakeAsync(CancellationToken cancellationToken = default)
    {
        if (_cts == null)
            throw new InvalidOperationException("Сессия чата не запущена.");

        _logger.LogInformation("Chat {ChatId}: manual crypto reset and handshake requested (role={Role})",
            _chat.Id, SessionRoleLabel());
        await ResetCryptoStateAsync(cancellationToken).ConfigureAwait(false);
        if (IsCryptoSessionLeader())
        {
            await EnsureSessionAsInitiatorAsync(cancellationToken).ConfigureAwait(false);
            _ = TryConfirmCryptoSessionAsync(cancellationToken);
        }
        else
        {
            await SendSessionSetupRequestPacketAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Временная отладка UI: chat invite (frame 0x30) на адреса пира.</summary>
    public async Task TechSendInviteAsync(CancellationToken cancellationToken = default)
    {
        if (_cts == null)
            throw new InvalidOperationException("Сессия чата не запущена.");

        await SendChatInviteWithRetryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Временная отладка UI: presence ping (порт discovery) на адреса пира.</summary>
    public async Task TechSendPresencePingAsync(CancellationToken cancellationToken = default)
    {
        if (_cts == null)
            throw new InvalidOperationException("Сессия чата не запущена.");

        var nid = CompressedNetworkId.FromShortString(_user.NetworkIdShort);
        var link = _routingSettings?.LinkTechnology ?? LinkTechnologyPreset.Unlimited;
        var ping = PresencePingCodec.Build(nid, _user.Nickname, _user.DataUdpPort, link);
        var sentUdpTargets = new HashSet<string>(StringComparer.Ordinal);

        var peers = BuildOrderedDirectPeerAddresses();
        peers.AddRange(BuildOrderedBroadcastAddresses());
        foreach (var addr in peers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (addr.Kind)
            {
                case TransportKind.Udp:
                {
                    var ipEp = UdpTransportAddress.ToIPEndPoint(addr);
                    var dest = UdpTransportAddress.FromIPEndPoint(
                        new IPEndPoint(ipEp.Address, PresencePingCodec.UdpPort));
                    var key = $"{ipEp.Address}:{PresencePingCodec.UdpPort}";
                    if (!sentUdpTargets.Add(key))
                        break;
                    var udp = ResolveOutbound(dest);
                    if (udp != null)
                        await udp.SendAsync(ping, dest, cancellationToken).ConfigureAwait(false);
                    break;
                }
                case TransportKind.Bluetooth when BluetoothTransport != null:
                    await BluetoothTransport.SendAsync(ping, addr, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        foreach (var ep in EnumerateIpv4BroadcastEndpoints(PresencePingCodec.UdpPort))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"{ep.Address}:{ep.Port}";
            if (!sentUdpTargets.Add(key))
                continue;
            var dest = UdpTransportAddress.FromIPEndPoint(ep);
            var udp = ResolveOutbound(dest);
            if (udp != null)
                await udp.SendAsync(ping, dest, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<IPEndPoint> EnumerateIpv4BroadcastEndpoints(int port)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address))
                    continue;
                var mask = ua.IPv4Mask;
                if (mask == null)
                    continue;
                var ep = new IPEndPoint(ComputeBroadcastAddress(ua.Address, mask), port);
                var key = $"{ep.Address}:{ep.Port}";
                if (seen.Add(key))
                    yield return ep;
            }
        }

        var limited = new IPEndPoint(IPAddress.Broadcast, port);
        var limitedKey = $"{limited.Address}:{limited.Port}";
        if (seen.Add(limitedKey))
            yield return limited;
    }

    private static IPAddress ComputeBroadcastAddress(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (a.Length != 4 || m.Length != 4)
            throw new ArgumentException("IPv4 address and mask are required.");
        var b = new byte[4];
        for (var i = 0; i < 4; i++)
            b[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(b);
    }

    private async Task SendChatInviteAsync(CancellationToken cancellationToken)
    {
        var host = BuildInviteHosts();
        var nid = CompressedNetworkId.FromShortString(_user.NetworkIdShort);
        var invite = ChatInviteCodec.Build(_user.Nickname, nid,
            RsaKeySerializer.SerializePublic(_auth.GetCurrentPublicKey()), host, ChatInviteCodec.InviteUdpPort);
        await SendInviteRouteRawAsync(invite, cancellationToken).ConfigureAwait(false);
        await SendSessionNegotiationAfterInviteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendSessionNegotiationAfterInviteAsync(CancellationToken cancellationToken)
    {
        if (IsCryptoSessionLeader())
        {
            _logger.LogInformation(
                "Chat {ChatId}: post-invite session negotiation as leader (crypto peer role={PeerRole}, BLE role={BleRole})",
                _chat.Id, PeerSessionRoleLabel(), BleSessionRoleLabel() ?? "n/a");
            await EnsureLeaderCryptoSessionCoreAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Chat {ChatId}: post-invite session negotiation as follower (crypto peer role={PeerRole}, BLE role={BleRole}, send 0x04)",
            _chat.Id, PeerSessionRoleLabel(), BleSessionRoleLabel() ?? "n/a");
        await SendSessionSetupRequestPacketAsync(cancellationToken).ConfigureAwait(false);
    }

    private string BuildInviteHosts()
    {
        return InviteHostsBuilder.BuildCommaSeparated(
            _routingSettings,
            _routingSettings?.EnableBluetoothTransport == false ? null : _routingSettings?.SelectedBluetoothAdapterMac,
            _user.NetworkIdShort,
            TimeSpan.FromSeconds(2));
    }

    private async Task SendChatInviteWithRetryAsync(CancellationToken cancellationToken)
    {
        const int fallbackAttempts = 3;
        var attempts = Math.Max(1, _routingSettings?.SendFailureSearchAttempts ?? fallbackAttempts);
        var delay = _routingSettings?.SendFailureRetryDelay ?? TimeSpan.FromMilliseconds(350);

        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SendChatInviteAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch when (i < attempts - 1)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsDeferrableSendFailure(Exception ex)
    {
        if (ex is InvalidOperationException ioe &&
            ioe.Message.Contains("P2P crypto session is not ready", StringComparison.Ordinal))
            return true;

        return ex switch
        {
            IOException => true,
            TimeoutException => true,
            SocketException => true,
            TaskCanceledException => true,
            _ => ex.InnerException != null && IsDeferrableSendFailure(ex.InnerException)
        };
    }

    private bool CanQueueUntilPeerSeenOnLan()
    {
        return _localNetworkScanner != null && !string.IsNullOrWhiteSpace(_chat.PeerNetworkIdShort);
    }

    private void HookPresenceForPendingFlush()
    {
        if (_presenceHooked)
            return;
        if (_localNetworkScanner != null)
        {
            _localNetworkScanner.ClientsChanged += OnLanClientsChangedForPendingFlush;
            _localNetworkScanner.DiscoveryPingReceived += OnDiscoveryPingForPendingFlush;
        }

        var hostTransports = _runtime.AdditionalTransports;
        if (hostTransports != null)
            hostTransports.PeerPresenceSeen += OnHostTransportPresenceForPendingFlush;

        _presenceHooked = true;
    }

    private void ClearPendingOutgoing()
    {
        lock (_pendingSync)
        {
            _pendingOutgoing.Clear();
        }
    }

    private void UnhookPresenceAndClearPending()
    {
        ClearPendingOutgoing();

        if (!_presenceHooked)
            return;
        if (_localNetworkScanner != null)
        {
            _localNetworkScanner.ClientsChanged -= OnLanClientsChangedForPendingFlush;
            _localNetworkScanner.DiscoveryPingReceived -= OnDiscoveryPingForPendingFlush;
        }

        var hostTransports = _runtime.AdditionalTransports;
        if (hostTransports != null)
            hostTransports.PeerPresenceSeen -= OnHostTransportPresenceForPendingFlush;

        _presenceHooked = false;
    }

    private void ResetOutboundCts()
    {
        _outboundCts?.Dispose();
        _outboundCts = _cts is { IsCancellationRequested: false }
            ? CancellationTokenSource.CreateLinkedTokenSource(_cts.Token)
            : null;
    }

    private async ValueTask CancelOutboundDeliveryAsync()
    {
        if (_outboundCts == null)
            return;
        try
        {
            await _outboundCts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            await _outboundCts.CancelAsync();
        }

        ResetOutboundCts();
    }

    private static CancellationTokenSource? CreateOutboundLinkedCts(CancellationToken cancellationToken,
        CancellationTokenSource? outboundCts)
    {
        if (outboundCts == null)
            return null;
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, outboundCts.Token);
    }

    /// <summary>Удаляет все сообщения чата из БД и отменяет недоставленные исходящие отправки.</summary>
    public async ValueTask<bool> ClearMessagesAsync(CancellationToken cancellationToken = default)
    {
        await CancelOutboundDeliveryAsync().ConfigureAwait(false);
        ClearPendingOutgoing();

        await _flushPendingSem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClearPendingOutgoing();
            var ok = await _repo.ClearMessagesAsync(_chat.Id, _user.Id, cancellationToken).ConfigureAwait(false);
            if (ok)
                RaiseMessagesChanged();
            return ok;
        }
        finally
        {
            _flushPendingSem.Release();
        }
    }

    private bool HasPendingOutgoing()
    {
        lock (_pendingSync)
        {
            return _pendingOutgoing.Count > 0;
        }
    }

    private void OnLanClientsChangedForPendingFlush(object? sender, EventArgs e)
    {
        if (!HasPendingOutgoing())
            return;
        // Peer is back online when seen on LAN or on a host transport (e.g. mesh presence within the freshness window).
        if (!_localNetworkScanner!.IsPeerSeenRecentlyOnLan(_chat.PeerNetworkIdShort) && !IsPeerSeenOnHostTransport())
            return;
        StartFlushPendingInBackground();
    }

    /// <summary>Mesh/LoRa peers ping once per minute: treat a peer seen by the host transport as online.</summary>
    private bool IsPeerSeenOnHostTransport()
    {
        var peerShort = _chat.PeerNetworkIdShort?.Trim();
        if (string.IsNullOrWhiteSpace(peerShort))
            return false;
        return _runtime.AdditionalTransports?.IsPeerRecentlySeen(peerShort) == true;
    }

    private void OnHostTransportPresenceForPendingFlush(object? sender, string peerNetworkIdShort)
    {
        if (string.IsNullOrWhiteSpace(_chat.PeerNetworkIdShort))
            return;
        if (!ChatRepository.PeerNetworkIdsEqual(peerNetworkIdShort, _chat.PeerNetworkIdShort))
            return;

        MergeAdditionalTransportPeerAddresses();
        PreferAdditionalTransportEndpoint();
        var token = _cts?.Token ?? CancellationToken.None;

        // Peer is on the mesh: keep LoRa handshake alive until probe/crypto is ready both sides.
        if (!IsCryptoSessionLeader() && !IsFollowerCryptoReady())
            _ = SendSessionSetupRequestQuietAsync(token);
        else if (IsCryptoSessionLeader() && !_cryptoProbeRoundTripOk)
            _ = ResendLeaderHandshakeQuietAsync(token);

        if (!HasPendingOutgoing())
            return;
        StartFlushPendingInBackground();
    }

    private async Task ResendLeaderHandshakeQuietAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureSessionAsInitiatorAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Chat {ChatId}: leader handshake resend on mesh presence failed", _chat.Id);
        }
    }

    private void OnDiscoveryPingForPendingFlush(object? sender, DiscoveryPingReceivedEventArgs e)
    {
        if (!HasPendingOutgoing())
            return;
        if (string.IsNullOrWhiteSpace(_chat.PeerNetworkIdShort))
            return;
        string peerShort;
        try
        {
            peerShort = e.Peer.NetworkId.ToShortString();
        }
        catch (FormatException)
        {
            return;
        }

        if (!string.Equals(peerShort, _chat.PeerNetworkIdShort.Trim(), StringComparison.OrdinalIgnoreCase))
            return;
        StartFlushPendingInBackground();
    }

    private void StartFlushPendingInBackground()
    {
        // Allow flush before StartAsync sets _cts (token = None). Only skip when the session
        // CTS exists and was cancelled (StopAsync / dispose) — otherwise outbound stays stuck.
        var cts = _cts;
        if (cts is { IsCancellationRequested: true })
            return;

        var token = cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await TryFlushPendingOutgoingAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Chat {ChatId}: outbound flush worker stopped", _chat.Id);
            }
        }, CancellationToken.None);
    }

    private async Task TryFlushPendingOutgoingAsync(CancellationToken cancellationToken)
    {
        await _flushPendingSem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _runtime.EnsureStartedAsync(_user, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                int nextId;
                lock (_pendingSync)
                {
                    if (_pendingOutgoing.Count == 0)
                        return;
                    nextId = _pendingOutgoing[0];
                }

                var row = await _repo.GetMessageAsync(nextId, includePayloadBlob: false).ConfigureAwait(false);
                if (row == null || row.ChatId != _chat.Id || !row.Outgoing)
                {
                    DequeuePendingOutgoingHead(nextId);
                    continue;
                }

                try
                {
                    if (OutgoingWireNeedsPayloadBlob(row))
                    {
                        row = await _repo.GetMessageAsync(nextId, includePayloadBlob: true).ConfigureAwait(false);
                        if (row == null || row.ChatId != _chat.Id || !row.Outgoing)
                        {
                            DequeuePendingOutgoingHead(nextId);
                            continue;
                        }
                    }

                    var wire = BuildOutgoingWire(row);
                    await DeliverOutgoingWireAsync(nextId, wire, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Keep Pending at head for a later retry / presence flush.
                    throw;
                }
                catch (Exception ex) when (IsUdpUnavailableError(ex) && _udpFallbackApplied == 0)
                {
                    _logger.LogWarning(ex,
                        "Chat {ChatId}: error while establishing connection by UDP, applying fallback",
                        _chat.Id);
                    _udpOutboundUnavailable = true;
                    await TryApplyUdpUnavailableWorkaroundAsync(cancellationToken, flushUnsent: false)
                        .ConfigureAwait(false);
                    await EnqueueLatestUnsentMessagesAsync(cancellationToken, startWorker: false)
                        .ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex) when (IsDeferrableSendFailure(ex) &&
                                           (CanQueueUntilPeerSeenOnLan() || HasHostTransportEndpoint()))
                {
                    _logger.LogInformation(ex,
                        "Chat {ChatId}: deferring outbound message {MessageId} (crypto/path not ready; LAN or LoRa presence will retry)",
                        _chat.Id, nextId);
                    MergeAdditionalTransportPeerAddresses();
                    PreferAdditionalTransportEndpoint();
                    HookPresenceForPendingFlush();
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Chat {ChatId}: outbound delivery failed for message {MessageId}",
                        _chat.Id, nextId);
                    try
                    {
                        await _repo.UpdateMessageDeliveryStatusAsync(nextId, MessageDeliveryStatus.Failed)
                            .ConfigureAwait(false);
                        RaiseMessagesChanged();
                    }
                    catch (Exception updateEx)
                    {
                        _logger.LogDebug(updateEx,
                            "Chat {ChatId}: failed to mark message {MessageId} as failed", _chat.Id, nextId);
                    }

                    DequeuePendingOutgoingHead(nextId);
                    continue;
                }

                DequeuePendingOutgoingHead(nextId);
            }
        }
        finally
        {
            _flushPendingSem.Release();
        }
    }

    private void DequeuePendingOutgoingHead(int messageId)
    {
        lock (_pendingSync)
        {
            if (_pendingOutgoing.Count > 0 && _pendingOutgoing[0] == messageId)
                _pendingOutgoing.RemoveAt(0);
        }
    }

    private static bool OutgoingWireNeedsPayloadBlob(ChatMessageEntity row) =>
        row.PayloadKind is (int)ChatPayloadKind.File or (int)ChatPayloadKind.Image;

    private byte[] BuildOutgoingWire(ChatMessageEntity row)
    {
        return row.PayloadKind switch
        {
            (int)ChatPayloadKind.File when row.ImageBlob == null || row.ImageBlob.Length == 0 =>
                throw new InvalidOperationException("File message has no payload."),
            (int)ChatPayloadKind.File => ChatWireCodec.EncodeFile(row.Text, row.MimeType, row.ImageBlob),
            (int)ChatPayloadKind.Image when row.ImageBlob == null || row.ImageBlob.Length == 0 =>
                throw new InvalidOperationException("Image message has no payload."),
            (int)ChatPayloadKind.Image => ChatWireCodec.EncodeImage(row.MimeType, row.ImageBlob),
            (int)ChatPayloadKind.TransferOffer => ChatWireCodec.EncodeTransferOffer(new ChatWireTransferOffer(
                row.TransferId,
                row.TransferToken,
                row.TransferPayloadKind,
                row.TransferFileName,
                row.MimeType,
                row.TransferSizeBytes,
                row.TransferHost,
                row.TransferPort,
                row.TransferExpiresUtcTicks,
                string.IsNullOrWhiteSpace(row.TransferId) ? null : row.TransferId)),
            _ => EncodeTextPreferringDictionary(row.Text)
        };
    }

    private byte[] EncodeTextPreferringDictionary(string text)
    {
        var raw = ChatWireCodec.EncodeText(text);
        Dictionary<string, VocabAcceptEntry>? accepted;
        lock (_sync)
            accepted = _vocabAccepted;
        if (accepted == null || !VocabularyCatalog.IsInitialized)
            return raw;

        foreach (var (lang, entry) in accepted)
        {
            if (entry.IsUncompressed || entry.Blake3Hex == null)
                continue;
            if (!VocabularyCatalog.Instance.TryGet(lang, entry.Version, entry.Blake3Hex, out var pack) ||
                pack == null)
                continue;
            var payload = DictionaryTextCodec.TryEncode(text, pack);
            if (payload == null)
                continue;
            var framed = ChatWireCodec.EncodeCompressedText(lang, entry.Version, payload);
            if (framed.Length < raw.Length)
                return framed;
        }

        return raw;
    }

    private bool TryDecodeCompressedText(ChatWireCompressedText compressed, out string text)
    {
        text = "";
        if (!VocabularyCatalog.IsInitialized)
            return false;
        Dictionary<string, VocabAcceptEntry>? accepted;
        lock (_sync)
            accepted = _vocabAccepted;
        var lang = compressed.Language.Trim().ToUpperInvariant();
        string? hash = null;
        if (accepted != null &&
            accepted.TryGetValue(lang, out var entry) &&
            !entry.IsUncompressed)
            hash = entry.Blake3Hex;
        // Follower may decode using local pack matching version before accept is stored on both sides.
        VocabularyPack? pack = null;
        if (hash != null &&
            VocabularyCatalog.Instance.TryGet(lang, compressed.DictVersion, hash, out pack) &&
            pack != null)
            return DictionaryTextCodec.TryDecode(compressed.Payload, pack, out text);

        pack = VocabularyCatalog.Instance.PacksForLanguage(lang)
            .FirstOrDefault(p => p.Version == compressed.DictVersion);
        return pack != null && DictionaryTextCodec.TryDecode(compressed.Payload, pack, out text);
    }

    private async Task HandleVocabOfferAsync(ChatWireVocabOffer offer, CancellationToken cancellationToken)
    {
        if (IsCryptoSessionLeader())
        {
            _logger.LogDebug("Chat {ChatId}: ignored vocab offer on leader", _chat.Id);
            return;
        }

        if (!VocabularyCatalog.IsInitialized ||
            !VocabularyNegotiation.TryParseOffer(offer.OfferText, out var parsed))
        {
            _logger.LogWarning("Chat {ChatId}: vocab offer parse failed", _chat.Id);
            return;
        }

        var accept = VocabularyNegotiation.SelectAccept(parsed, VocabularyCatalog.Instance);
        lock (_sync)
            _vocabAccepted = accept;
        var acceptText = VocabularyNegotiation.FormatAccept(accept);
        _logger.LogInformation("Chat {ChatId}: vocab accept → {Accept}", _chat.Id, acceptText);
        var wire = ChatWireCodec.EncodeVocabAccept(acceptText);
        try
        {
            await DeliverControlWireAsync(wire, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: vocab accept send failed", _chat.Id);
        }
    }

    private void HandleVocabAccept(ChatWireVocabAccept accept)
    {
        if (!IsCryptoSessionLeader())
        {
            _logger.LogDebug("Chat {ChatId}: ignored vocab accept on follower", _chat.Id);
            return;
        }

        if (!VocabularyNegotiation.TryParseAccept(accept.AcceptText, out var parsed))
        {
            _logger.LogWarning("Chat {ChatId}: vocab accept parse failed", _chat.Id);
            return;
        }

        lock (_sync)
            _vocabAccepted = parsed;
        _logger.LogInformation("Chat {ChatId}: vocab negotiation locked ← {Accept}", _chat.Id, accept.AcceptText);
    }

    private async Task ShareVocabOfferAfterEstablishAsync(CancellationToken cancellationToken)
    {
        if (!IsCryptoSessionLeader() || !VocabularyCatalog.IsInitialized)
            return;
        try
        {
            var offer = VocabularyCatalog.Instance.BuildOffer();
            if (offer.Count == 0)
                return;
            var offerText = VocabularyNegotiation.FormatOffer(offer);
            _logger.LogInformation("Chat {ChatId}: vocab offer → {Offer}", _chat.Id, offerText);
            var wire = ChatWireCodec.EncodeVocabOffer(offerText);
            await DeliverControlWireAsync(wire, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: vocab offer send failed", _chat.Id);
        }
    }

    private async Task DeliverControlWireAsync(byte[] wire, CancellationToken cancellationToken)
    {
        // Best-effort control frame over the same encrypted messenger path as UserInfo.
        if (_messenger == null)
            return;
        var dests = BuildOrderedDirectPeerAddresses();
        if (dests.Count > 0)
            await _messenger.SendBinaryAsyncExpectAck(wire, dests, cancellationToken).ConfigureAwait(false);
        else if (_peerAddress != null)
            await _messenger.SendBinaryAsync(wire, _peerAddress, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeliverOutgoingWireAsync(int messageId, byte[] wire, CancellationToken cancellationToken)
    {
        SubscribeToTransceivers();
        using var linkedCts = CreateOutboundLinkedCts(cancellationToken, _outboundCts);
        var deliveryToken = linkedCts?.Token ?? cancellationToken;

        string? serverMessageId = null;
        await _guaranteedDelivery.ExecuteAsync(
            async ct =>
            {
                var acceptedId = await TryServerDeliveryAsync(wire, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(acceptedId))
                {
                    // Register immediately so a fast peer receipt can correlate even if we crash
                    // before the outer Sent update.
                    await _repo.RegisterOutgoingServerMessageAsync(acceptedId, messageId, _chat.Id)
                        .ConfigureAwait(false);
                    serverMessageId = acceptedId;
                    return;
                }

                await EnsureSessionAsInitiatorAsync(ct).ConfigureAwait(false);
                if (_handshakeWeInitiated && !_cryptoProbeRoundTripOk)
                    _ = TryConfirmCryptoSessionAsync(ct);

                // Do not enter WaitForCryptoSessionAsync (infinite poll) when handshake is incomplete —
                // that used to freeze the whole outbound flush after a server receive/send race.
                if (_messenger == null || !TryGetCryptoSession(out _))
                    throw new InvalidOperationException("P2P crypto session is not ready for outbound delivery.");

                MergeAdditionalTransportPeerAddresses();
                if (string.IsNullOrEmpty(_chat.RelayRouteBlob))
                {
                    var dests = BuildOrderedDirectPeerAddresses();
                    if (dests.Count == 0)
                        throw new InvalidOperationException(
                            "No ready peer endpoints for P2P delivery (UDP/BLE/LoRa).");
                    _logger.LogInformation(
                        "Chat {ChatId}: P2P send message {MessageId} wireLen={WireLen} via {DestCount} endpoint(s): {Endpoints}",
                        _chat.Id, messageId, wire.Length, dests.Count,
                        string.Join(", ", dests.Select(FormatTransportAddress)));
                    // One check while waiting for the peer delivery ACK.
                    await _repo.UpdateMessageDeliveryStatusAsync(messageId, MessageDeliveryStatus.Sent)
                        .ConfigureAwait(false);
                    RaiseMessagesChanged();
                    await _messenger.SendBinaryAsyncExpectAck(wire, dests, ct).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Chat {ChatId}: delivery ACK received for message {MessageId}",
                        _chat.Id, messageId);
                }
                else
                {
                    _logger.LogInformation(
                        "Chat {ChatId}: P2P relay send message {MessageId} via {Endpoint}",
                        _chat.Id, messageId, FormatTransportAddress(_peerAddress!));
                    await _messenger.SendBinaryAsync(wire, _peerAddress!, ct).ConfigureAwait(false);
                }
            },
            null,
            false,
            _routingSettings,
            deliveryToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(serverMessageId))
        {
            // Server accepted — one checkmark (Sent). Delivered waits for the peer receipt.
            await _repo.UpdateMessageDeliveryStatusAsync(messageId, MessageDeliveryStatus.Sent)
                .ConfigureAwait(false);
        }
        else
        {
            // P2P path: ExpectAck completed with a real peer ACK (or relay send finished).
            await _repo.UpdateMessageDeliveryStatusAsync(messageId, MessageDeliveryStatus.Delivered)
                .ConfigureAwait(false);
        }

        RaiseMessagesChanged();
    }

    private async Task SendWireAsync(byte[] wire, CancellationToken cancellationToken)
    {
        using var linkedCts = CreateOutboundLinkedCts(cancellationToken, _outboundCts);
        var deliveryToken = linkedCts?.Token ?? cancellationToken;

        var ackTimeout = (_routingSettings?.LinkTechnology ?? LinkTechnologyPreset.Unlimited).GetMessageAckTimeout();
        await _guaranteedDelivery.ExecuteAsync(
            async ct =>
            {
                if (!string.IsNullOrEmpty(await TryServerDeliveryAsync(wire, ct).ConfigureAwait(false)))
                    return;

                await EnsureSessionAsInitiatorAsync(ct).ConfigureAwait(false);
                if (_handshakeWeInitiated && !_cryptoProbeRoundTripOk)
                    _ = TryConfirmCryptoSessionAsync(ct);

                if (_messenger == null || !TryGetCryptoSession(out _))
                    throw new InvalidOperationException("P2P crypto session is not ready for outbound delivery.");

                if (string.IsNullOrEmpty(_chat.RelayRouteBlob))
                {
                    var dests = BuildOrderedDirectPeerAddresses();
                    await _messenger.SendBinaryAsyncExpectAck(wire, dests, ct).ConfigureAwait(false);
                }
                else
                {
                    await _messenger.SendBinaryAsync(wire, _peerAddress!, ct).ConfigureAwait(false);
                }
            },
            null,
            false,
            _routingSettings,
            deliveryToken).ConfigureAwait(false);
    }

    /// <summary>TRL-7: push local AboutMe + Avatar to this chat peer (not stored in chat history).</summary>
    public async Task SendLocalUserInfoAsync(CancellationToken cancellationToken = default)
    {
        var user = _auth.CurrentUser ?? _user;
        var about = user.AboutMe ?? "";
        var wire = ChatWireCodec.EncodeUserInfo(about, user.Avatar ?? Array.Empty<byte>());
        await SendWireAsync(wire, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyIncomingUserInfoAsync(ChatWireUserInfo info, CancellationToken cancellationToken)
    {
        var store = _runtime.PeerProfiles;
        if (store == null)
            return;

        try
        {
            var peerId = CompressedNetworkId.FromShortString(_chat.PeerNetworkIdShort);
            var nick = string.IsNullOrWhiteSpace(_chat.PeerNickname) ? null : _chat.PeerNickname.Trim();
            await store.UpsertAsync(peerId, nick, info.AboutMe ?? "", info.Avatar, cancellationToken)
                .ConfigureAwait(false);
            _runtime.LocalScan.ApplyCachedAboutMe(peerId, info.AboutMe ?? "");
            _logger.LogInformation(
                "Chat {ChatId}: applied peer UserInfo (aboutLen={AboutLen}, avatarBytes={AvatarBytes})",
                _chat.Id, info.AboutMe?.Length ?? 0, info.Avatar?.Length ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: failed to apply incoming UserInfo (best-effort)", _chat.Id);
        }
    }

    private void MarkCryptoSessionEstablished(CancellationToken cancellationToken)
    {
        var firstTime = !_cryptoProbeRoundTripOk;
        _cryptoProbeRoundTripOk = true;
        RefreshHandshakeStatus();
        if (!firstTime || _userInfoSharedAfterEstablish)
            return;
        _userInfoSharedAfterEstablish = true;
        _ = ShareLocalUserInfoAfterEstablishAsync(cancellationToken);
        _ = ShareVocabOfferAfterEstablishAsync(cancellationToken);
    }

    private async Task ShareLocalUserInfoAfterEstablishAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SendLocalUserInfoAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Chat {ChatId}: shared local UserInfo after session establish", _chat.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _userInfoSharedAfterEstablish = false;
            _logger.LogWarning(ex, "Chat {ChatId}: UserInfo share after establish failed (best-effort)", _chat.Id);
        }
    }

    public async ValueTask RetryFailedMessageAsync(int messageId, CancellationToken cancellationToken = default)
    {
        var row = await _repo.GetMessageAsync(messageId).ConfigureAwait(false);
        if (row == null || row.ChatId != _chat.Id || !row.Outgoing)
            return;

        await _repo.UpdateMessageDeliveryStatusAsync(messageId, MessageDeliveryStatus.Pending).ConfigureAwait(false);
        RaiseMessagesChanged();
        var wire = BuildOutgoingWire(row);
        QueueOutgoingDelivery(messageId, wire, cancellationToken);
    }

    public async ValueTask SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (text.Length > MaxMessageChars)
            throw new ArgumentException($"Message is too long. Max length is {MaxMessageChars} characters.",
                nameof(text));

        var messageId = await _repo.AddMessageAsync(_chat.Id, true, text, MessageDeliveryStatus.Pending)
            .ConfigureAwait(false);
        RaiseMessagesChanged();

        var wire = EncodeTextPreferringDictionary(text);
        QueueOutgoingDelivery(messageId, wire, cancellationToken);
    }

    /// <summary>
    ///     Enqueue for the single per-chat outbound worker. Receive stays independent;
    ///     parallel fire-and-forget delivers were starving BT/UDP inbound and racing session setup.
    /// </summary>
    private void QueueOutgoingDelivery(int messageId, byte[] wire, CancellationToken cancellationToken)
    {
        // Wire is rebuilt from DB in the flush worker so retries stay consistent with stored payload.
        _ = wire;
        _ = cancellationToken;
        lock (_pendingSync)
        {
            if (!_pendingOutgoing.Contains(messageId))
                _pendingOutgoing.Add(messageId);
        }

        HookPresenceForPendingFlush();
        StartFlushPendingInBackground();
    }

    public async ValueTask SendImageAsync(ReadOnlyMemory<byte> imageBytes, string mimeType,
        CancellationToken cancellationToken = default)
    {
        if (imageBytes.Length == 0)
            throw new ArgumentException("Image is empty.", nameof(imageBytes));

        var bytes = imageBytes.ToArray();
        if (_chat.IsBot)
        {
            BotAttachmentPolicy.ValidateSend(mimeType, bytes.Length, CurrentTrafficQuality, _media);
            await QueueBotBinaryAsync("image", "image", mimeType, bytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        _media.ValidateMime(mimeType);
        _media.ValidateSize(imageBytes.Length, CurrentTrafficQuality);
        await CreateAndSendTransferOfferAsync("image", "image", mimeType, bytes, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask SendFileAsync(string fileName, ReadOnlyMemory<byte> fileBytes, string mimeType,
        CancellationToken cancellationToken = default)
    {
        if (fileBytes.Length == 0)
            throw new ArgumentException("File is empty.", nameof(fileBytes));

        var bytes = fileBytes.ToArray();
        var safeName = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrEmpty(safeName))
            safeName = "file";

        if (_chat.IsBot)
        {
            BotAttachmentPolicy.ValidateSend(mimeType, bytes.Length, CurrentTrafficQuality, _media);
            var botKind = BotAttachmentPolicy.TryClassify(mimeType, out var classified)
                ? classified
                : BotAttachmentKind.Document;
            var payloadKind = botKind switch
            {
                BotAttachmentKind.Image => "image",
                BotAttachmentKind.Audio => "audio",
                BotAttachmentKind.Video => "video",
                _ => "document"
            };
            await QueueBotBinaryAsync(payloadKind, safeName, mimeType, bytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        _media.ValidateDocumentMime(mimeType);
        _media.ValidateFileSize(mimeType, fileBytes.Length, CurrentTrafficQuality);

        var payloadKindHuman = mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            ? "voice"
            : mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                ? "video"
                : "document";
        await CreateAndSendTransferOfferAsync(payloadKindHuman, safeName, mimeType, bytes, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RequestBinaryDownloadAsync(int messageId, CancellationToken cancellationToken = default)
    {
        var row = await _repo.GetMessageAsync(messageId).ConfigureAwait(false);
        if (row == null || row.ChatId != _chat.Id || row.Outgoing || string.IsNullOrWhiteSpace(row.TransferId))
            return;

        var state = (ChatTransferState)row.TransferState;
        if (state == ChatTransferState.Received && row.ImageBlob is { Length: > 0 })
        {
            if (_chat.IsBot && row.TransferChunkCount > 0)
                await ReceiptBotBlobAsync(row, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (state == ChatTransferState.Transferring)
            return;

        await _repo.UpdateTransferStateAsync(messageId, ChatTransferState.Transferring).ConfigureAwait(false);
        TransferStateChanged?.Invoke(this, messageId);
        RaiseMessagesChanged();

        try
        {
            await _runtime.EnsureStartedAsync(_user, cancellationToken).ConfigureAwait(false);
            SubscribeToTransceivers();
            await StartAsync(cancellationToken).ConfigureAwait(false);

            if (await TryReceiveBlobFromMessengerServersAsync(row, cancellationToken).ConfigureAwait(false))
                return;

            if (_chat.IsBot)
                throw new InvalidOperationException("Не удалось скачать вложение бота.");

            var localHost = TryResolveLocalHost() ?? "127.0.0.1";
            var lease = await _tcpTransfer.CreateListenerAsync(row.TransferId, row.TransferToken, TimeSpan.FromSeconds(45),
                cancellationToken).ConfigureAwait(false);
            try
            {
                var ack = new ChatWireTransferControl("tcp-ack", row.TransferId, row.TransferToken, localHost, lease.Port,
                    lease.ExpiresAtUtc.UtcTicks, "");
                await SendWireAsync(ChatWireCodec.EncodeTransferControl(ack), cancellationToken)
                    .ConfigureAwait(false);
                var bytes = await _tcpTransfer.AcceptAndReceiveAsync(lease, row.TransferSizeBytes, cancellationToken)
                    .ConfigureAwait(false);
                var targetKind = row.TransferPayloadKind.Equals("image", StringComparison.OrdinalIgnoreCase)
                    ? ChatPayloadKind.Image
                    : ChatPayloadKind.File;
                var fileName = string.IsNullOrWhiteSpace(row.TransferFileName) ? row.Text : row.TransferFileName;
                await _repo.UpdateMessagePayloadAsync(messageId, targetKind, fileName, row.MimeType, bytes)
                    .ConfigureAwait(false);
                await _repo.UpdateMessageTransferMetadataAsync(messageId, row.TransferId, row.TransferToken,
                        row.TransferPayloadKind, row.TransferFileName, row.TransferSizeBytes, "", 0, 0,
                        ChatTransferState.Received)
                    .ConfigureAwait(false);
                TransferStateChanged?.Invoke(this, messageId);
                RaiseMessagesChanged();
            }
            finally
            {
                lease.Dispose();
            }
        }
        catch
        {
            await _repo.UpdateTransferStateAsync(messageId, ChatTransferState.Failed).ConfigureAwait(false);
            TransferStateChanged?.Invoke(this, messageId);
            RaiseMessagesChanged();
            throw;
        }
    }

    private async Task<bool> TryReceiveBlobFromMessengerServersAsync(
        ChatMessageEntity row,
        CancellationToken cancellationToken)
    {
        var servers = _runtime.MessengerServers;
        if (servers == null)
            return false;

        var blobId = row.TransferId.Trim();
        if (blobId.Length == 0)
            return false;

        var hint = LooksLikeHttpBaseUrl(row.TransferHost) ? row.TransferHost : null;
        byte[]? ciphertext;
        if (_chat.IsBot && row.TransferChunkCount > 0)
        {
            ciphertext = await servers.TryDownloadBotChunksAsync(
                    blobId,
                    hint,
                    row.TransferChunkCount,
                    row.TransferToken,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            ciphertext = await servers.TryDownloadBlobAsync(
                    blobId, hint, cancellationToken, botServersOnly: _chat.IsBot)
                .ConfigureAwait(false);
        }

        if (ciphertext == null || ciphertext.Length == 0)
            return false;

        byte[] wire;
        try
        {
            wire = MessengerServerPayloadCodec.Decrypt(ciphertext, _auth.GetCurrentPrivateKey());
        }
        catch
        {
            return false;
        }

        if (!ChatWireCodec.TryParse(wire, out var parsed) || parsed == null)
            return false;

        byte[] bytes;
        string mimeType;
        string fileName;
        ChatPayloadKind kind;
        switch (parsed)
        {
            case ChatWireImage img:
                if (!_chat.IsBot)
                {
                    _media.ValidateMime(img.MimeType);
                    _media.ValidateSize(img.ImageBytes.Length, CurrentTrafficQuality);
                }

                bytes = img.ImageBytes;
                mimeType = img.MimeType;
                fileName = string.IsNullOrWhiteSpace(row.TransferFileName) ? row.Text : row.TransferFileName;
                kind = ChatPayloadKind.Image;
                break;
            case ChatWireFile f:
                if (!_chat.IsBot)
                {
                    _media.ValidateDocumentMime(f.MimeType);
                    _media.ValidateFileSize(f.MimeType, f.FileBytes.Length, CurrentTrafficQuality);
                }

                bytes = f.FileBytes;
                mimeType = f.MimeType;
                fileName = string.IsNullOrWhiteSpace(f.FileName)
                    ? (string.IsNullOrWhiteSpace(row.TransferFileName) ? row.Text : row.TransferFileName)
                    : f.FileName;
                kind = ChatPayloadKind.File;
                break;
            default:
                return false;
        }

        await _repo.UpdateMessagePayloadAsync(row.Id, kind, fileName, mimeType, bytes).ConfigureAwait(false);
        await _repo.UpdateMessageTransferMetadataAsync(
                row.Id, row.TransferId, row.TransferToken, row.TransferPayloadKind, row.TransferFileName,
                bytes.Length, "", 0, 0, ChatTransferState.Received)
            .ConfigureAwait(false);
        TransferStateChanged?.Invoke(this, row.Id);
        RaiseMessagesChanged();

        try
        {
            if (_chat.IsBot && row.TransferChunkCount > 0)
                await ReceiptBotBlobAsync(row, cancellationToken).ConfigureAwait(false);
            else
                await servers.TryDeleteBlobAsync(blobId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Local receive already succeeded; TTL will drop leftovers.
        }

        return true;
    }

    private static bool LooksLikeHttpBaseUrl(string? host) =>
        !string.IsNullOrWhiteSpace(host) &&
        (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
         host.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    private async Task QueueBotBinaryAsync(
        string payloadKind,
        string fileName,
        string mimeType,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var messageId = await _repo
            .AddFileMessageAsync(_chat.Id, true, fileName, mimeType, bytes, MessageDeliveryStatus.Pending)
            .ConfigureAwait(false);
        var serverMessageId = Guid.NewGuid().ToString("N");
        var expires = DateTimeOffset.UtcNow.AddMinutes(2);
        await _repo.PromoteFileToTransferOfferAsync(
                messageId, serverMessageId, "", payloadKind, fileName, mimeType, bytes.Length, expires.UtcTicks)
            .ConfigureAwait(false);
        RaiseMessagesChanged();

        var innerWire = payloadKind.Equals("image", StringComparison.OrdinalIgnoreCase)
            ? ChatWireCodec.EncodeImage(mimeType, bytes)
            : ChatWireCodec.EncodeFile(fileName, mimeType, bytes);

        _ = _runtime.BinarySends.RunAsync(
            CurrentTrafficQuality,
            ct => CompleteBotBinaryAsync(
                messageId, serverMessageId, innerWire, payloadKind, fileName, mimeType, bytes.Length, expires.UtcTicks, ct),
            cancellationToken);
    }

    private async Task CompleteBotBinaryAsync(
        int messageId,
        string serverMessageId,
        byte[] innerWire,
        string payloadKind,
        string fileName,
        string mimeType,
        int plaintextLength,
        long expiresUtcTicks,
        CancellationToken cancellationToken)
    {
        try
        {
            var servers = _runtime.MessengerServers ??
                          throw new InvalidOperationException("Messenger-серверы не подключены.");
            var uploaded = await servers
                .TryUploadBotChunksAsync(_chat, _user, serverMessageId, innerWire, cancellationToken)
                .ConfigureAwait(false);
            if (uploaded == null)
                throw new InvalidOperationException("Не удалось выложить вложение на messenger-сервер.");

            await _repo.UpdateBotChunkLocatorAsync(
                    messageId, uploaded.BaseUrl, uploaded.TotalChunks, uploaded.CiphertextSha256)
                .ConfigureAwait(false);

            var offer = new ChatWireTransferOffer(
                serverMessageId,
                uploaded.CiphertextSha256,
                payloadKind,
                fileName,
                mimeType,
                plaintextLength,
                uploaded.BaseUrl,
                0,
                expiresUtcTicks,
                serverMessageId,
                uploaded.TotalChunks,
                uploaded.CiphertextSha256);
            var accepted = await servers.TryDeliverBotWireAsync(
                    _chat,
                    _user,
                    ChatWireCodec.EncodeTransferOffer(offer),
                    cancellationToken,
                    serverMessageId)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(accepted))
                throw new InvalidOperationException("Не удалось доставить сообщение боту через messenger-сервер.");

            await _repo.RegisterOutgoingServerMessageAsync(accepted, messageId, _chat.Id).ConfigureAwait(false);
            await _repo.UpdateMessageDeliveryStatusAsync(messageId, MessageDeliveryStatus.Sent).ConfigureAwait(false);
            RaiseMessagesChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: bot binary send failed for message {MessageId}", _chat.Id, messageId);
            try
            {
                await _repo.UpdateMessageDeliveryStatusAsync(messageId, MessageDeliveryStatus.Failed)
                    .ConfigureAwait(false);
                await _repo.UpdateTransferStateAsync(messageId, ChatTransferState.Failed).ConfigureAwait(false);
                RaiseMessagesChanged();
            }
            catch (Exception updateEx)
            {
                _logger.LogDebug(updateEx, "Chat {ChatId}: failed to mark bot binary {MessageId} as failed", _chat.Id,
                    messageId);
            }
        }
    }

    private async Task ReceiptBotBlobAsync(ChatMessageEntity row, CancellationToken cancellationToken)
    {
        var servers = _runtime.MessengerServers;
        if (servers == null || string.IsNullOrWhiteSpace(row.TransferId))
            return;
        var hint = LooksLikeHttpBaseUrl(row.TransferHost) ? row.TransferHost : null;
        await servers.TrySubmitBotMessageReceiptAsync(row.TransferId.Trim(), hint, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task CreateAndSendTransferOfferAsync(string payloadKind, string fileName, string mimeType,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var messageId = await _repo
            .AddFileMessageAsync(_chat.Id, true, fileName, mimeType, bytes, MessageDeliveryStatus.Pending)
            .ConfigureAwait(false);
        var transferId = Guid.NewGuid().ToString("N");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var expires = DateTimeOffset.UtcNow.AddMinutes(2);
        // One column UPDATE — do not Find+Update (would rewrite ImageBlob and freeze SQLite).
        await _repo.PromoteFileToTransferOfferAsync(
                messageId, transferId, token, payloadKind, fileName, mimeType, bytes.Length, expires.UtcTicks)
            .ConfigureAwait(false);
        RaiseMessagesChanged();

        var innerWire = payloadKind.Equals("image", StringComparison.OrdinalIgnoreCase)
            ? ChatWireCodec.EncodeImage(mimeType, bytes)
            : ChatWireCodec.EncodeFile(fileName, mimeType, bytes);
        var offer = new ChatWireTransferOffer(transferId, token, payloadKind, fileName, mimeType, bytes.Length, "", 0,
            expires.UtcTicks, transferId);
        _ = CompleteTransferOfferInBackgroundAsync(messageId, innerWire, offer, cancellationToken);
    }

    private async Task CompleteTransferOfferInBackgroundAsync(
        int messageId,
        byte[] innerWire,
        ChatWireTransferOffer offer,
        CancellationToken cancellationToken)
    {
        try
        {
            var servers = _runtime.MessengerServers;
            if (servers != null)
            {
                try
                {
                    using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    // Cap blob PUT so a hung upload cannot leave the chat Pending forever /
                    // starve the command HttpClient.
                    uploadCts.CancelAfter(TimeSpan.FromMinutes(2));
                    await servers.TryUploadBlobAsync(_chat, _user, offer.TransferId, innerWire, uploadCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Chat {ChatId}: blob upload timed out for message {MessageId}; sending offer anyway",
                        _chat.Id, messageId);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // TCP remains the fallback when no server accepted the blob.
                }
            }

            QueueOutgoingDelivery(messageId, ChatWireCodec.EncodeTransferOffer(offer), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Leave Pending so a later retry can finish delivery.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: background transfer offer failed for message {MessageId}", _chat.Id,
                messageId);
            try
            {
                await _repo.UpdateMessageDeliveryStatusAsync(messageId, MessageDeliveryStatus.Failed)
                    .ConfigureAwait(false);
                await _repo.UpdateTransferStateAsync(messageId, ChatTransferState.Failed).ConfigureAwait(false);
                RaiseMessagesChanged();
            }
            catch (Exception updateEx)
            {
                _logger.LogDebug(updateEx, "Chat {ChatId}: failed to mark transfer {MessageId} as failed", _chat.Id,
                    messageId);
            }
        }
    }

    private async Task HandleTransferOfferAsync(ChatWireTransferOffer offer, string? blobServerBaseUrl = null)
    {
        var text = string.IsNullOrWhiteSpace(offer.FileName) ? "[Входящее вложение]" : offer.FileName;
        var host = !string.IsNullOrWhiteSpace(offer.Host) ? offer.Host : blobServerBaseUrl?.Trim() ?? "";
        await _repo.AddIncomingTransferOfferAsync(
                _chat.Id,
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
    }

    private async Task HandleTransferControlAsync(ChatWireTransferControl control, CancellationToken cancellationToken)
    {
        if (!string.Equals(control.Command, "tcp-ack", StringComparison.OrdinalIgnoreCase))
            return;
        // Do NOT ListMessagesAsync (loads every ImageBlob). Resolve by TransferId only.
        var meta = await _repo.FindOutgoingTransferByIdAsync(_chat.Id, control.TransferId).ConfigureAwait(false);
        if (meta == null)
            return;
        if (!string.Equals(meta.TransferToken, control.TransferToken, StringComparison.Ordinal))
            return;
        if (string.IsNullOrWhiteSpace(control.Host) || control.Port is < 1 or > 65535)
            return;

        var row = await _repo.GetMessageAsync(meta.Id, includePayloadBlob: true).ConfigureAwait(false);
        if (row?.ImageBlob is not { Length: > 0 })
            return;

        await _repo.UpdateTransferStateAsync(row.Id, ChatTransferState.Transferring).ConfigureAwait(false);
        TransferStateChanged?.Invoke(this, row.Id);
        try
        {
            await _tcpTransfer.SendAsync(control.Host, control.Port, row.TransferId, row.TransferToken, row.ImageBlob,
                cancellationToken).ConfigureAwait(false);
            await _repo.UpdateTransferStateAsync(row.Id, ChatTransferState.Received).ConfigureAwait(false);
        }
        catch
        {
            await _repo.UpdateTransferStateAsync(row.Id, ChatTransferState.Failed).ConfigureAwait(false);
            throw;
        }
        finally
        {
            TransferStateChanged?.Invoke(this, row.Id);
        }
    }

    private string? TryResolveLocalHost()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        return ua.Address.ToString();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>Сброс AES-сессии и повторный обмен ключами после ошибки дешифровки входящего пакета.</summary>
    private async ValueTask OnDecryptFailureAsync()
    {
        if (Interlocked.CompareExchange(ref _decryptRecoveryGate, 1, 0) != 0)
            return;
        try
        {
            var token = _cts?.Token ?? CancellationToken.None;
            if (token.IsCancellationRequested)
                return;

            var now = DateTimeOffset.UtcNow;
            if (now - _lastDecryptRecoveryUtc < DecryptRecoveryCooldown)
                return;
            _lastDecryptRecoveryUtc = now;
            _logger.LogWarning("Chat {ChatId}: decrypt failure, starting crypto recovery (role={Role})",
                _chat.Id, SessionRoleLabel());

            await ResetCryptoStateAsync(token).ConfigureAwait(false);
            if (!IsCryptoSessionLeader())
            {
                // Follower only clears AES and nudges; leader drives invite + test_send retries.
                try
                {
                    await SendSessionSetupRequestPacketAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Chat {ChatId}: follower 0x04 after decrypt failure failed", _chat.Id);
                }

                return;
            }

            await SendChatInviteWithRetryAsync(token).ConfigureAwait(false);
            await EnsureSessionAsInitiatorAsync(token).ConfigureAwait(false);
            _ = TryConfirmCryptoSessionAsync(token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Chat {ChatId}: decrypt recovery canceled", _chat.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: decrypt recovery failed", _chat.Id);
        }
        finally
        {
            Interlocked.Exchange(ref _decryptRecoveryGate, 0);
        }
    }

    private async Task ResetCryptoStateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Chat {ChatId}: resetting crypto state (role={Role})", _chat.Id, SessionRoleLabel());
        if (_messenger != null)
        {
            _messenger.GotData -= OnMessengerGotData;
            await _messenger.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_sync)
        {
            _cryptoProbeOkAwaiter?.TrySetCanceled();
            _cryptoProbeOkAwaiter = null;
            _outstandingTestSendHash = null;
            CancelFollowerHandshakeWait();
            _handshakeWeInitiated = false;
            _cryptoProbeRoundTripOk = false;
            _userInfoSharedAfterEstablish = false;
            _messenger = null;
            _pendingInboundCipher = null;
            _pendingInboundCipherFrom = null;
            _vocabAccepted = null;
        }

        ClearCryptoSession();
        RefreshHandshakeStatus();
    }

    /// <summary>
    ///     Invite (0x30) на все адреса пира по каждому включённому виду транспорта (UDP invite-порт, Bluetooth).
    ///     Ошибка только если не удалось отправить ни по одному транспорту.
    /// </summary>
    private async ValueTask SendInviteRouteRawAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        var destinations = BuildOrderedDirectPeerAddresses();
        if (destinations.Count == 0)
            throw new InvalidOperationException("Нет адресов пира для отправки invite.");

        var hadAttempt = false;
        var anySuccess = false;
        Exception? last = null;

        foreach (var dest in destinations)
        {
            if (!IsTransportEnabled(dest.Kind))
                continue;
            if (dest.Kind == TransportKind.Udp && _udpOutboundUnavailable)
                continue;

            hadAttempt = true;
            try
            {
                await SendInviteOnPeerAddressAsync(packet, dest, cancellationToken).ConfigureAwait(false);
                anySuccess = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                if (dest.Kind == TransportKind.Udp && IsUdpUnavailableError(ex))
                    _udpOutboundUnavailable = true;
            }
        }

        if (!hadAttempt)
            throw new InvalidOperationException("Нет включённых транспортов для отправки invite.");

        if (!anySuccess)
            throw new InvalidOperationException("Все транспорты недоступны: не удалось отправить invite.", last);
    }

    private async ValueTask SendInviteOnPeerAddressAsync(ReadOnlyMemory<byte> packet, TransportAddress destination,
        CancellationToken cancellationToken)
    {
        switch (destination.Kind)
        {
            case TransportKind.Udp:
                if (!IsTransportEnabled(TransportKind.Udp))
                    throw new InvalidOperationException("UDP-транспорт отключён в настройках.");
                var inviteTx = _runtime.Invite
                               ?? throw new InvalidOperationException(
                                   "UDP invite-транспорт недоступен (слушатель не запущен).");
                var inviteDest = ToInviteUdpDestination(destination);
                var nid = CompressedNetworkId.FromShortString(_user.NetworkIdShort);
                var inviteMsg = new InviteMessage(nid, _user.Nickname, "", "", 0, packet, inviteDest);
                await inviteTx.SendAsync(inviteMsg, inviteDest, cancellationToken).ConfigureAwait(false);
                return;

            case TransportKind.Bluetooth:
                if (!IsTransportEnabled(TransportKind.Bluetooth))
                    throw new InvalidOperationException("Bluetooth-транспорт отключён в настройках.");
                var bt = ResolveOutbound(destination)
                         ?? throw new InvalidOperationException("Bluetooth-транспорт недоступен.");
                await bt.SendAsync(packet, destination, cancellationToken).ConfigureAwait(false);
                return;

            default:
                if (!IsTransportEnabled(destination.Kind))
                    throw new InvalidOperationException($"Транспорт {destination.Kind} отключён.");
                var extra = ResolveOutbound(destination)
                            ?? throw new InvalidOperationException($"Транспорт {destination.Kind} недоступен.");
                await extra.SendAsync(packet, destination, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private static TransportAddress ToInviteUdpDestination(TransportAddress destination)
    {
        var ep = UdpTransportAddress.ToIPEndPoint(destination);
        return UdpTransportAddress.FromIPEndPoint(new IPEndPoint(ep.Address, ChatInviteCodec.InviteUdpPort));
    }

    /// <summary>
    ///     Отправка «сырых» пакетов handshake (0x01/0x04) на пира. Перебирает peer endpoints
    ///     в порядке предпочтения и шлёт на <b>все</b> готовые транспорты (UDP + BLE + LoRa).
    ///     UDP SendAsync часто «успешен» в никуда; early-return оставлял LoRa без 0x01/0x04.
    /// </summary>
    private async ValueTask SendRouteRawAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        Exception? last = null;
        var anySuccess = false;
        foreach (var dest in BuildOrderedDirectPeerAddresses())
        {
            var transport = ResolveOutbound(dest);
            if (transport == null)
                continue;
            try
            {
                var frame = packet.Length > 0 ? packet.Span[0] : (byte)0;
                _logger.LogInformation(
                    "Chat {ChatId}: TX raw 0x{Frame:X2} ({Bytes} bytes) → {Endpoint}",
                    _chat.Id, frame, packet.Length, FormatTransportAddress(dest));
                await transport.SendAsync(packet, dest, cancellationToken).ConfigureAwait(false);
                anySuccess = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                _logger.LogWarning(ex,
                    "Chat {ChatId}: TX raw failed → {Endpoint}",
                    _chat.Id, FormatTransportAddress(dest));
                if (dest.Kind == TransportKind.Udp && IsUdpUnavailableError(ex))
                    _udpOutboundUnavailable = true;
            }
        }

        if (!anySuccess && last != null)
            throw last;
    }

    private Task SendInviteRawAsync(ReadOnlyMemory<byte> payload, TransportAddress destination,
        CancellationToken cancellationToken)
    {
        return SendInviteOnPeerAddressAsync(payload, destination, cancellationToken).AsTask();
    }

    /// <summary>
    ///     Отправка cipher payload (без префикса 0x02) через глобальный <see cref="MessageTransceiver" />.
    ///     Делегируется в <see cref="MessengerService" /> при каждом исходящем сообщении/чанке.
    /// </summary>
    private async ValueTask SendCipherAsync(ReadOnlyMemory<byte> cipherPayload, TransportAddress destination,
        CancellationToken cancellationToken)
    {
        var message = _runtime.Message
                      ?? throw new InvalidOperationException("Message transceiver is not initialized.");
        _logger.LogInformation(
            "Chat {ChatId}: TX cipher {Bytes} bytes → {Endpoint}",
            _chat.Id, cipherPayload.Length, FormatTransportAddress(destination));
        await message.SendAsync(new TransportReceiveMessage(cipherPayload, destination), destination,
            cancellationToken).ConfigureAwait(false);
    }

    private ITransport? ResolveOutbound(TransportAddress destination)
    {
        return destination.Kind switch
        {
            TransportKind.Udp when IsUdpOutboundReady() => _runtime.DataUdp,
            TransportKind.Bluetooth when IsTransportEnabled(TransportKind.Bluetooth) => BluetoothTransport,
            _ when IsTransportEnabled(destination.Kind) =>
                _runtime.AdditionalTransports?.ResolveOutbound(destination),
            _ => null
        };
    }

    private async Task ProcessHandshakePacketAsync(ReadOnlyMemory<byte> body, TransportAddress remoteAddress,
        CancellationToken cancellationToken)
    {
        if (body.Length != 128)
        {
            _logger.LogWarning(
                "Chat {ChatId}: ignored RSA handshake from {Remote}, invalid body length {Length}",
                _chat.Id, FormatTransportAddress(remoteAddress), body.Length);
            return;
        }

        PreferEndpoint(remoteAddress);
        await HandleResponderHandshakeAsync(body.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessSessionSetupRequestAsync(ReadOnlyMemory<byte> body, TransportAddress remoteAddress,
        CancellationToken cancellationToken)
    {
        if (!IsCryptoSessionLeader())
        {
            _logger.LogDebug(
                "Chat {ChatId}: ignored session setup request from {Remote} (not leader)",
                _chat.Id, FormatTransportAddress(remoteAddress));
            return;
        }

        // Body is payload after the 0x04 marker (see DataPortMultiplexer Slice(1)).
        if (body.Length != CompressedNetworkId.WireLength)
        {
            _logger.LogWarning(
                "Chat {ChatId}: ignored session setup request from {Remote}, invalid body length {Length}",
                _chat.Id, FormatTransportAddress(remoteAddress), body.Length);
            return;
        }

        var peerId = CompressedNetworkId.FromWireBytes(body.Span);
        var expected = CompressedNetworkId.FromShortString(_chat.PeerNetworkIdShort.Trim());
        if (peerId != expected)
        {
            _logger.LogWarning(
                "Chat {ChatId}: ignored session setup request from {Remote}, network id mismatch (got {Got}, expected {Expected})",
                _chat.Id, FormatTransportAddress(remoteAddress), peerId.ToShortString(), expected.ToShortString());
            return;
        }

        // Reply on the same path the nudge arrived on (e.g. LoRa when UDP is dead).
        PreferEndpoint(remoteAddress);
        _logger.LogInformation(
            "Chat {ChatId}: accepted session setup request from {Remote}, sending RSA handshake",
            _chat.Id, FormatTransportAddress(remoteAddress));
        await EnsureLeaderCryptoSessionCoreAsync(cancellationToken, true)
            .ConfigureAwait(false);
    }

    private List<TransportAddress> BuildOrderedDirectPeerAddresses()
    {
        //в _peerAddress должен записываться адрес, который приходит в пакете с networkId
        var list = new List<TransportAddress>();
        if (_peerAddress != null && IsOutboundTransportReady(_peerAddress.Kind))
            list.Add(_peerAddress);
        foreach (var ep in _peerEndpoints)
        {
            if (!IsOutboundTransportReady(ep.Kind))
                continue;
            if (list.Any(x => x.Kind == ep.Kind && x.Data.AsSpan().SequenceEqual(ep.Data)))
                continue;
            list.Add(ep);
        }

        return list;
    }


    private List<TransportAddress> BuildOrderedBroadcastAddresses()
    {
        var endpoints = LanBroadcastHelper.GetIpv4BroadcastEndpoints(17501); // EnumerateBroadcastAddresses

        return endpoints.Select(UdpTransportAddress.FromIPEndPoint).ToList();
    }

    /// <summary>
    ///     Leader: send test_send → wait for matching test_response_for_&lt;hash&gt;;
    ///     on timeout — pause 5 s ± 2 s, reset crypto, invite, handshake, retry.
    ///     Follower replies to test_send with test_response. Not stored in chat history.
    /// </summary>
    private async Task TryConfirmCryptoSessionAsync(CancellationToken cancellationToken)
    {
        if (_cryptoProbeRoundTripOk)
            return;
        // Hard gate: followers must never enter the test_send / renegotiation retry loop.
        if (!CryptoSessionLeadership.ShouldDriveSessionVerification(IsCryptoSessionLeader()))
            return;

        if (!await _cryptoProbeLoopLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            if (!_handshakeWeInitiated || !IsCryptoSessionLeader())
                return;

            SetHandshakeStatus(ChatHandshakeStatus.InProgress);
            _logger.LogInformation(
                "Chat {ChatId}: starting crypto probe round-trip confirmation (role={Role})",
                _chat.Id, SessionRoleLabel());
            var okWait = TimeSpan.FromSeconds(60);
            var softProbeRetries = 0;
            const int maxSoftProbeRetries = 2;

            while (!cancellationToken.IsCancellationRequested && !_cryptoProbeRoundTripOk)
            {
                MessengerService? ms;
                lock (_sync)
                {
                    ms = _messenger;
                }

                var canProbe = ms != null && _handshakeWeInitiated && IsCryptoSessionLeader();
                if (canProbe)
                {
                    var my = _user.NetworkIdShort.Trim();
                    var peer = _chat.PeerNetworkIdShort.Trim();
                    var testWire = ChatWireCodec.EncodeText(
                        SessionCryptoProbe.FormatTestSend(my, peer, DateTimeOffset.UtcNow, out var sendHash));

                    TaskCompletionSource<bool> tcs;
                    lock (_sync)
                    {
                        _outstandingTestSendHash = sendHash;
                        _cryptoProbeOkAwaiter?.TrySetCanceled();
                        _cryptoProbeOkAwaiter = tcs =
                            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    try
                    {
                        _logger.LogInformation(
                            "Chat {ChatId}: sending crypto probe test_send hash={Hash}",
                            _chat.Id, sendHash);
                        await SendEncryptedProbeWireAsync(testWire, cancellationToken).ConfigureAwait(false);
                        await tcs.Task.WaitAsync(okWait, cancellationToken).ConfigureAwait(false);
                        MarkCryptoSessionEstablished(cancellationToken);
                        _logger.LogInformation(
                            "Chat {ChatId}: crypto probe round-trip confirmed (test_response for {Hash})",
                            _chat.Id, sendHash);
                        return;
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning(
                            "Chat {ChatId}: crypto probe test_response not received within {TimeoutSeconds}s",
                            _chat.Id, okWait.TotalSeconds);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogDebug("Chat {ChatId}: crypto probe canceled", _chat.Id);
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Chat {ChatId}: crypto probe send/wait failed", _chat.Id);
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            if (ReferenceEquals(_cryptoProbeOkAwaiter, tcs))
                                _cryptoProbeOkAwaiter = null;
                            if (string.Equals(_outstandingTestSendHash, sendHash, StringComparison.OrdinalIgnoreCase))
                                _outstandingTestSendHash = null;
                        }
                    }
                }

                if (_cryptoProbeRoundTripOk)
                    return;

                var pauseSec = 5 + Random.Shared.Next(-2, 3);
                _logger.LogInformation(
                    "Chat {ChatId}: crypto probe failed, retrying session setup after {PauseSeconds}s",
                    _chat.Id, pauseSec);
                await Task.Delay(TimeSpan.FromSeconds(pauseSec), cancellationToken).ConfigureAwait(false);

                try
                {
                    // LoRa/host: probe ACK is often lost to duty-cycle/chunking. Keep AES keys and re-TX 0x01
                    // instead of wiping a session the follower may already be using to encrypt chat.
                    byte[]? softResend = null;
                    lock (_sync)
                    {
                        if (softProbeRetries < maxSoftProbeRetries
                            && _runtime.AdditionalTransports != null
                            && TryGetCryptoSession(out _)
                            && _messenger != null
                            && _lastLeaderHandshakeFrame is { Length: > 0 } frame)
                        {
                            softResend = frame;
                        }
                    }

                    if (softResend != null)
                    {
                        softProbeRetries++;
                        _logger.LogInformation(
                            "Chat {ChatId}: crypto probe soft retry {Attempt}/{Max} — resend cached 0x01 without resetting keys",
                            _chat.Id, softProbeRetries, maxSoftProbeRetries);
                        _lastLeaderHandshakeSentUtc = DateTimeOffset.UtcNow;
                        await SendRouteRawAsync(softResend, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    softProbeRetries = 0;

                    await ResetCryptoStateAsync(cancellationToken).ConfigureAwait(false);
                    if (_deliveryPath != ChatDeliveryPath.Server)
                        await SendChatInviteWithRetryAsync(cancellationToken).ConfigureAwait(false);
                    else
                        await TryServerFallbackAsync(cancellationToken).ConfigureAwait(false);
                    await EnsureSessionAsInitiatorAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Chat {ChatId}: crypto probe recovery cycle failed", _chat.Id);
                }
            }
        }
        finally
        {
            _cryptoProbeLoopLock.Release();
        }
    }

    private async Task SendEncryptedProbeWireAsync(byte[] wire, CancellationToken cancellationToken)
    {
        var m = _messenger ?? throw new InvalidOperationException("Messenger is not initialized.");
        if (string.IsNullOrEmpty(_chat.RelayRouteBlob))
        {
            var dests = BuildOrderedDirectPeerAddresses();
            Exception? last = null;
            foreach (var d in dests)
                try
                {
                    await m.SendBinaryAsync(wire, d, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                }

            if (last != null)
                throw last;
            throw new IOException("No direct peer address for crypto probe.");
        }

        await m.SendBinaryAsync(wire, _peerAddress!, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendCryptoProbeTestResponseAsync(string testSendHash, CancellationToken cancellationToken)
    {
        try
        {
            var m = _messenger;
            if (m == null)
                return;
            var my = _user.NetworkIdShort.Trim();
            var peer = _chat.PeerNetworkIdShort.Trim();
            var wire = ChatWireCodec.EncodeText(
                SessionCryptoProbe.FormatTestResponse(my, peer, testSendHash, DateTimeOffset.UtcNow, out _));
            _logger.LogInformation(
                "Chat {ChatId}: sending crypto probe test_response_for_{Hash}",
                _chat.Id, testSendHash);
            await SendEncryptedProbeWireAsync(wire, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: crypto probe test_response send failed", _chat.Id);
        }
    }

    private bool TryHandleSessionCryptoProbeText(string text, CancellationToken cancellationToken)
    {
        if (!SessionCryptoProbe.TryParse(text, out var message))
            return false;
        var my = _user.NetworkIdShort.Trim();
        var peer = _chat.PeerNetworkIdShort.Trim();
        if (!string.Equals(message.TargetNetworkId, my, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(message.SourceNetworkId, peer, StringComparison.OrdinalIgnoreCase))
            return false;

        bool weInitiated;
        string? outstandingHash;
        lock (_sync)
        {
            weInitiated = _handshakeWeInitiated;
            outstandingHash = _outstandingTestSendHash;
        }

        if (message.Kind == SessionCryptoProbeKind.TestSend)
        {
            if (weInitiated)
                return true; // leader ignores inbound test_send
            _logger.LogInformation(
                "Chat {ChatId}: received crypto probe test_send hash={Hash}, sending test_response",
                _chat.Id, message.ContentHash);
            _ = SendCryptoProbeTestResponseAsync(message.ContentHash, cancellationToken);
            // Decrypting a valid test_send proves the follower path; mark confirmed for UI.
            MarkCryptoSessionEstablished(cancellationToken);
            return true;
        }

        if (message.Kind == SessionCryptoProbeKind.TestResponse)
        {
            if (!weInitiated)
                return true;
            var referenced = message.ReferencedTestSendHash ?? "";
            if (string.IsNullOrEmpty(outstandingHash) ||
                !string.Equals(referenced, outstandingHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Chat {ChatId}: crypto probe test_response hash mismatch (got={Got}, expected={Expected})",
                    _chat.Id, referenced, outstandingHash ?? "(none)");
                return true;
            }

            _logger.LogInformation(
                "Chat {ChatId}: received matching crypto probe test_response_for_{Hash}",
                _chat.Id, referenced);
            TaskCompletionSource<bool>? w;
            lock (_sync)
            {
                w = _cryptoProbeOkAwaiter;
                _cryptoProbeOkAwaiter = null;
                _outstandingTestSendHash = null;
            }

            w?.TrySetResult(true);
            return true;
        }

        return false;
    }

    private async Task SendSessionSetupRequestPacketAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Chat {ChatId}: sending session setup request (0x04), crypto role={Role}, BLE role={BleRole}",
            _chat.Id, SessionRoleLabel(), BleSessionRoleLabel() ?? "n/a");
        var id = CompressedNetworkId.FromShortString(_user.NetworkIdShort.Trim());
        var buf = new byte[1 + CompressedNetworkId.WireLength];
        buf[0] = FrameSessionSetupRequest;
        if (!id.TryWriteBytes(buf.AsSpan(1, CompressedNetworkId.WireLength)))
            throw new InvalidOperationException("Failed to write network id.");
        await SendRouteRawAsync(buf, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Follower: send 0x04 a few times while waiting for the leader's 0x01.
    ///     LoRa is lossy; a single nudge at start is often not enough.
    /// </summary>
    private async Task NudgeLeaderSessionSetupAsync(CancellationToken cancellationToken)
    {
        // Immediate + 2s + 5s + 12s ≈ four attempts over ~20s.
        int[] delaysMs = [0, 2000, 5000, 12000];
        for (var i = 0; i < delaysMs.Length; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            if (IsFollowerCryptoReady())
                return;

            if (delaysMs[i] > 0)
            {
                try
                {
                    await Task.Delay(delaysMs[i], cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (IsFollowerCryptoReady())
                return;

            // Refresh mesh endpoints before each nudge — directory may learn the peer mid-flight.
            MergeAdditionalTransportPeerAddresses();
            PreferAdditionalTransportEndpoint();

            try
            {
                await SendSessionSetupRequestPacketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Chat {ChatId}: follower 0x04 nudge {Attempt} failed", _chat.Id, i + 1);
            }
        }
    }

    /// <summary>
    ///     Leader crypto setup: holds <see cref="_sessionSetup" /> only for local state mutation.
    ///     Handshake bytes are sent after release so inbound 0x01/0x04/cipher are not starved.
    /// </summary>
    /// <param name="forceSendHandshake">Ответ на 0x04 от follower — шлём 0x01; при уже живой сессии — тот же пакет, без ротации ключей.</param>
    private async Task EnsureLeaderCryptoSessionCoreAsync(CancellationToken cancellationToken,
        bool forceSendHandshake = false)
    {
        byte[]? packetToSend = null;
        MessengerService? msToStart = null;
        var resendCachedOnly = false;

        await _sessionSetup.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                var ready = TryGetCryptoSession(out _) && _messenger != null;
                if (!forceSendHandshake && ready)
                {
                    _logger.LogDebug("Chat {ChatId}: leader crypto session already active, skip handshake",
                        _chat.Id);
                    return;
                }

                // Duplicate 0x04 on LoRa: keep AES keys the follower already accepted; only re-TX the same 0x01.
                if (forceSendHandshake && ready && _lastLeaderHandshakeFrame is { Length: > 0 } cached)
                {
                    var since = DateTimeOffset.UtcNow - _lastLeaderHandshakeSentUtc;
                    if (since < LeaderHandshakeResendMinInterval)
                    {
                        _logger.LogDebug(
                            "Chat {ChatId}: suppressing duplicate 0x01 resend (cooldown {Elapsed:F1}s < {Min:F0}s)",
                            _chat.Id, since.TotalSeconds, LeaderHandshakeResendMinInterval.TotalSeconds);
                        return;
                    }

                    packetToSend = cached;
                    resendCachedOnly = true;
                    _logger.LogInformation(
                        "Chat {ChatId}: leader re-sending cached RSA handshake (0x01) without rotating keys, role={Role}",
                        _chat.Id, SessionRoleLabel());
                }
            }

            if (!resendCachedOnly)
            {
                if (_peerPublicKey == null)
                {
                    _logger.LogWarning("Chat {ChatId}: cannot send leader handshake — peer public key missing",
                        _chat.Id);
                    return;
                }

                _logger.LogInformation(
                    "Chat {ChatId}: leader sending RSA handshake (0x01), crypto role={Role}, BLE role={BleRole}, force={ForceSendHandshake}",
                    _chat.Id, SessionRoleLabel(), BleSessionRoleLabel() ?? "n/a", forceSendHandshake);
                var hs = P2PCrypto.CreateHandshakeInitiation(_peerPublicKey);
                var packet = new byte[129];
                packet[0] = FrameHandshake;
                Buffer.BlockCopy(hs.HandshakePacket, 0, packet, 1, hs.HandshakePacket.Length);

                lock (_sync)
                {
                    var ready = TryGetCryptoSession(out _) && _messenger != null;
                    if (!forceSendHandshake && ready)
                        return;

                    // Race: session became ready with a cached frame while we built a new initiation — prefer cache.
                    if (forceSendHandshake && ready && _lastLeaderHandshakeFrame is { Length: > 0 } cached)
                    {
                        packetToSend = cached;
                    }
                    else
                    {
                        // New keys only when there is no usable session yet (or after an explicit reset).
                        if (ready || TryGetCryptoSession(out _))
                            ClearCryptoSession();

                        _ = _cryptoSessionCache.GetSession(_chat.Id, () => hs.Session);
                        _lastLeaderHandshakeFrame = packet;
                        _logger.LogInformation("Chat {ChatId}: leader crypto session created in cache (role={Role})",
                            _chat.Id,
                            SessionRoleLabel());
                        if (_messenger == null)
                        {
                            _messenger =
                                new MessengerService(SendCipherAsync, WaitForCryptoSessionAsync, CreateMessengerOptions(),
                                    OnDecryptFailureAsync);
                            _messenger.GotData += OnMessengerGotData;
                            msToStart = _messenger;
                        }

                        _handshakeWeInitiated = true;
                        packetToSend = packet;
                    }
                }
            }
        }
        finally
        {
            _sessionSetup.Release();
        }

        if (packetToSend != null)
        {
            _lastLeaderHandshakeSentUtc = DateTimeOffset.UtcNow;
            await SendRouteRawAsync(packetToSend, cancellationToken).ConfigureAwait(false);
        }

        if (msToStart != null)
        {
            await msToStart.StartAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Chat {ChatId}: messenger started (crypto role={Role}, BLE role={BleRole})",
                _chat.Id, SessionRoleLabel(), BleSessionRoleLabel() ?? "n/a");
            TryFlushPendingInboundCipher();
        }
        else if (packetToSend != null)
        {
            TryFlushPendingInboundCipher();
        }
    }

    private async Task EnsureSessionAsInitiatorAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Chat {ChatId}: ensure session as initiator ({Role})", _chat.Id, SessionRoleLabel());
        await EnsureMessengerStartedForExistingSessionAsync(cancellationToken).ConfigureAwait(false);

        if (IsCryptoSessionLeader())
        {
            _logger.LogDebug("Chat {ChatId}: leader session setup begin", _chat.Id);
            await EnsureLeaderCryptoSessionCoreAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Chat {ChatId}: leader session setup completed (role={Role})", _chat.Id,
                SessionRoleLabel());
        }

        // if (IsFollowerCryptoReady())
        // {
        //     _logger.LogDebug("Chat {ChatId}: follower crypto already ready", _chat.Id);
        //     return;
        // }
        //
        // TaskCompletionSource<bool> waitHandshake;
        // var shouldSendSessionRequest = false;
        // lock (_sync)
        // {
        //     if (IsFollowerCryptoReadyCore())
        //         return;
        //
        //     if (_followerHandshakeTcs is { Task.IsCompleted: false })
        //     {
        //         _logger.LogDebug("Chat {ChatId}: follower waiting on existing handshake flight", _chat.Id);
        //         waitHandshake = _followerHandshakeTcs;
        //     }
        //     else
        //     {
        //         waitHandshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        //         _followerHandshakeTcs = waitHandshake;
        //         shouldSendSessionRequest = true;
        //     }
        // }
        //
        // if (IsFollowerCryptoReady())
        // {
        //     SignalFollowerHandshakeSuccess(waitHandshake);
        //     return;
        // }
        //
        // if (shouldSendSessionRequest)
        //     try
        //     {
        //         await SendSessionSetupRequestPacketAsync(cancellationToken).ConfigureAwait(false);
        //     }
        //     catch (Exception ex)
        //     {
        //         SignalFollowerHandshakeFailure(ex, waitHandshake);
        //         throw;
        //     }
        //
        // if (IsFollowerCryptoReady())
        // {
        //     SignalFollowerHandshakeSuccess(waitHandshake);
        //     return;
        // }
        //
        // try
        // {
        //     _logger.LogDebug("Chat {ChatId}: follower waiting for RSA handshake (timeout 60s)", _chat.Id);
        //     await waitHandshake.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        //     _logger.LogInformation("Chat {ChatId}: follower session setup completed", _chat.Id);
        // }
        // catch (TimeoutException ex)
        // {
        //     _logger.LogWarning(ex, "Chat {ChatId}: follower handshake wait timed out", _chat.Id);
        //     SignalFollowerHandshakeFailure(ex, waitHandshake);
        //     throw;
        // }
        // finally
        // {
        //     lock (_sync)
        //     {
        //         if (ReferenceEquals(_followerHandshakeTcs, waitHandshake))
        //             _followerHandshakeTcs = null;
        //     }
        // }
    }

    private async Task EnsureMessengerStartedForExistingSessionAsync(CancellationToken cancellationToken)
    {
        MessengerService? created = null;
        lock (_sync)
        {
            if (!TryGetCryptoSession(out _) || _messenger != null)
                return;
            _messenger =
                new MessengerService(SendCipherAsync, WaitForCryptoSessionAsync, CreateMessengerOptions(),
                    OnDecryptFailureAsync);
            _messenger.GotData += OnMessengerGotData;
            created = _messenger;
        }

        await created.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Chat {ChatId}: messenger started (crypto role={Role}, BLE role={BleRole}) from cached crypto session",
            _chat.Id, SessionRoleLabel(), BleSessionRoleLabel() ?? "n/a");
        TryFlushPendingInboundCipher();
    }

    private MessengerOptions CreateMessengerOptions()
    {
        var ack = (_routingSettings?.LinkTechnology ?? LinkTechnologyPreset.Unlimited).GetMessageAckTimeout();
        // Host transports (LoRa) need a much longer ACK window than LAN UDP.
        if (_runtime.AdditionalTransports != null)
            ack = TimeSpan.FromSeconds(Math.Max(ack.TotalSeconds, 60));
        return new MessengerOptions
        {
            MaxBinaryMessageBytes = _media.MaxMessengerBinaryBytes,
            DeliveryAckTimeout = ack
        };
    }

    private async Task HandleResponderHandshakeAsync(byte[] handshakePacket, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Chat {ChatId}: processing RSA handshake packet (crypto role={Role}, BLE role={BleRole})",
            _chat.Id, SessionRoleLabel(), BleSessionRoleLabel() ?? "n/a");

        MessengerService? created = null;
        await _sessionSetup.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsCryptoSessionLeader())
            {
                _logger.LogDebug("Chat {ChatId}: ignored RSA handshake on leader side", _chat.Id);
                return;
            }

            lock (_sync)
            {
                // Identical 0x01 resend (leader keeps keys): do not rebuild session / reset messenger counters.
                if (_lastAcceptedHandshakePayload != null
                    && handshakePacket.AsSpan().SequenceEqual(_lastAcceptedHandshakePayload)
                    && IsFollowerCryptoReadyCore())
                {
                    _logger.LogDebug(
                        "Chat {ChatId}: ignoring identical RSA handshake resend; follower already ready",
                        _chat.Id);
                    SignalFollowerHandshakeSuccess();
                    return;
                }

                ClearCryptoSession();
                var localPrivate = _auth.GetCurrentPrivateKey();
                _ = _cryptoSessionCache.GetSession(_chat.Id,
                    () => P2PCrypto.CreateSession(localPrivate, handshakePacket));
                _lastAcceptedHandshakePayload = handshakePacket.ToArray();
                _logger.LogInformation(
                    "Chat {ChatId}: crypto session created from handshake (role={Role})",
                    _chat.Id, SessionRoleLabel());

                _handshakeWeInitiated = false;
                if (_messenger == null)
                {
                    _messenger =
                        new MessengerService(SendCipherAsync, WaitForCryptoSessionAsync, CreateMessengerOptions(),
                            OnDecryptFailureAsync);
                    _messenger.GotData += OnMessengerGotData;
                    created = _messenger;
                }
            }

            SignalFollowerHandshakeSuccess();
        }
        finally
        {
            _sessionSetup.Release();
        }

        if (created != null)
        {
            await created.StartAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Chat {ChatId}: messenger started (crypto role={Role}, BLE role={BleRole})",
                _chat.Id, SessionRoleLabel(), BleSessionRoleLabel() ?? "n/a");
        }

        TryFlushPendingInboundCipher();
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        LogSessionRoleContext("P2P session stopping");
        UnhookPresenceAndClearPending();
        UnsubscribeFromTransceivers();
        _startFinished = false;

        if (_cts != null)
            await _cts.CancelAsync();

        if (_messenger != null)
        {
            _messenger.GotData -= OnMessengerGotData;
            await _messenger.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        _cts?.Dispose();
        _cts = null;
        _outboundCts?.Dispose();
        _outboundCts = null;
        lock (_sync)
        {
            _cryptoProbeOkAwaiter?.TrySetCanceled();
            _cryptoProbeOkAwaiter = null;
            CancelFollowerHandshakeWait();
            _handshakeWeInitiated = false;
            _cryptoProbeRoundTripOk = false;
            _userInfoSharedAfterEstablish = false;
        }

        _messenger = null;
        ClearCryptoSession();
        LogSessionRoleContext("P2P session stopped");
    }

    private bool IsTransportEnabled(byte kind)
    {
        return kind switch
        {
            TransportKind.Udp => _routingSettings?.EnableUdpTransport ?? true,
            TransportKind.Bluetooth => _routingSettings?.EnableBluetoothTransport ?? true,
            _ => _runtime.AdditionalTransports?.IsEnabled(kind) ?? false
        };
    }

    private bool IsUdpOutboundReady() =>
        IsTransportEnabled(TransportKind.Udp) && !_udpOutboundUnavailable && _runtime.DataUdp != null;

    private bool IsOutboundTransportReady(byte kind) =>
        kind == TransportKind.Udp ? IsUdpOutboundReady() : IsTransportEnabled(kind);

    private bool IsUdpConnectionUnavailable() =>
        _udpOutboundUnavailable || !_runtime.IsUdpTransportEstablished;

    private static bool IsUdpUnavailableError(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is SocketException)
                return true;
            if (current.Message.Contains("UDP", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     SHO-10 / TRL-30 workaround. UDP did not come up: try BLE and host transports (e.g. LoRa)
    ///     when enabled and the peer is reachable there, and independently pin the chat to an available
    ///     messenger server that lists this subscriber. Send prefers that server (existing server-first
    ///     delivery); receive stays on the server long-poll. When both paths are up, server delivery still
    ///     wins if the peer is registered there; BLE/LoRa remain the P2P paths.
    /// </summary>
    private async Task TryApplyUdpUnavailableWorkaroundAsync(CancellationToken cancellationToken, bool flushUnsent)
    {
        if (Interlocked.Exchange(ref _udpFallbackApplied, 1) == 1)
            return;

        _udpOutboundUnavailable = true;
        if (AllowsAutomaticServerFallback(_deliveryPath))
            await TryServerFallbackAsync(cancellationToken).ConfigureAwait(false);
        await TryBleFallbackAsync(cancellationToken).ConfigureAwait(false);
        await TryAdditionalTransportFallbackAsync(cancellationToken).ConfigureAwait(false);
        if (flushUnsent)
            await EnqueueLatestUnsentMessagesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TryAdditionalTransportFallbackAsync(CancellationToken cancellationToken)
    {
        MergeAdditionalTransportPeerAddresses();
        if (!PreferAdditionalTransportEndpoint())
        {
            _logger.LogInformation(
                "Chat {ChatId}: host-transport fallback skipped (no enabled LoRa/mesh endpoint for peer)",
                _chat.Id);
            return;
        }

        _logger.LogInformation(
            "Chat {ChatId}: UDP unavailable, preferring host transport {Endpoint} for session setup",
            _chat.Id, FormatTransportAddress(_peerAddress!));

        if (!IsCryptoSessionLeader())
            return;

        try
        {
            await SendChatInviteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: host-transport invite during UDP fallback failed", _chat.Id);
        }
    }

    /// <summary>
    ///     Pull MeshDirectory / host-transport addresses for this peer into the live route so session
    ///     setup can leave on LoRa even before chat-row endpoint sync lands.
    /// </summary>
    private void MergeAdditionalTransportPeerAddresses()
    {
        var provider = _runtime.AdditionalTransports;
        var peerShort = _chat.PeerNetworkIdShort?.Trim();
        if (provider == null || string.IsNullOrWhiteSpace(peerShort))
            return;

        IReadOnlyList<TransportAddress> extras;
        try
        {
            extras = provider.GetPeerAddresses(peerShort);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Chat {ChatId}: GetPeerAddresses failed", _chat.Id);
            return;
        }

        if (extras.Count == 0)
            return;

        foreach (var ep in extras)
        {
            if (!IsTransportEnabled(ep.Kind))
                continue;
            if (_peerEndpoints.Any(x => x.Kind == ep.Kind && x.Data.AsSpan().SequenceEqual(ep.Data)))
                continue;
            _peerEndpoints.Add(ep);
        }
    }

    private bool HasHostTransportEndpoint()
    {
        MergeAdditionalTransportPeerAddresses();
        foreach (var ep in _peerEndpoints)
        {
            if (ep.Kind is TransportKind.Udp or TransportKind.Bluetooth or TransportKind.MessengerServer)
                continue;
            if (IsOutboundTransportReady(ep.Kind))
                return true;
        }

        return IsPeerSeenOnHostTransport();
    }

    /// <returns>True when a non-UDP/BT host endpoint was promoted to primary.</returns>
    private bool PreferAdditionalTransportEndpoint()
    {
        TransportAddress? preferred = null;
        foreach (var ep in _peerEndpoints)
        {
            if (ep.Kind is TransportKind.Udp or TransportKind.Bluetooth or TransportKind.MessengerServer)
                continue;
            if (!IsOutboundTransportReady(ep.Kind))
                continue;
            preferred = ep;
            break;
        }

        if (preferred == null)
            return false;

        PreferEndpoint(preferred);
        return true;
    }

    private void PreferEndpoint(TransportAddress endpoint)
    {
        _peerAddress = endpoint;
        _peerEndpoints.RemoveAll(ep =>
            ep.Kind == endpoint.Kind && ep.Data.AsSpan().SequenceEqual(endpoint.Data));
        _peerEndpoints.Insert(0, endpoint);
    }

    private async Task TryBleFallbackAsync(CancellationToken cancellationToken)
    {
        var peerHasMac = TryGetPeerBluetoothMac(out _);
        if (!ShouldTryBleFallback(
                IsTransportEnabled(TransportKind.Bluetooth),
                BluetoothTransport != null,
                peerHasMac))
        {
            _logger.LogInformation(
                "Chat {ChatId}: BLE fallback skipped (enabled={Enabled}, transport={HasTransport}, peerMac={HasPeer})",
                _chat.Id,
                IsTransportEnabled(TransportKind.Bluetooth),
                BluetoothTransport != null,
                peerHasMac);
            return;
        }

        PreferBleEndpoint();
        _logger.LogInformation(
            "Chat {ChatId}: UDP unavailable, switching invite to BLE {PeerMac}",
            _chat.Id, FormatTransportAddress(_peerAddress!));
        try
        {
            await SendChatInviteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: BLE invite during UDP fallback failed", _chat.Id);
        }
    }

    private async Task<bool> TryServerFallbackAsync(CancellationToken cancellationToken)
    {
        var servers = _runtime.MessengerServers;
        if (servers == null)
        {
            _logger.LogInformation("Chat {ChatId}: messenger-server fallback skipped (servers not configured)",
                _chat.Id);
            return false;
        }

        try
        {
            var bound = await servers.TrySwitchPeerToHostingServerAsync(_chat.PeerNetworkIdShort, cancellationToken)
                .ConfigureAwait(false);
            if (bound == null)
            {
                _logger.LogInformation(
                    "Chat {ChatId}: no available messenger server hosts peer {PeerId}",
                    _chat.Id, _chat.PeerNetworkIdShort);
                return false;
            }

            _activeMessengerServerBaseUrl = bound.BaseUrl;
            _logger.LogInformation(
                "Chat {ChatId}: switched send/receive to messenger server {BaseUrl}",
                _chat.Id, bound.BaseUrl);
            RaiseActiveChannelChanged();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: messenger-server fallback failed", _chat.Id);
            return false;
        }
    }

    private void PreferBleEndpoint()
    {
        if (!TryGetPeerBluetoothMac(out var mac))
            return;
        PreferEndpoint(BluetoothTransportAddress.FromMac(mac));
    }

    private async Task EnqueueLatestUnsentMessagesAsync(CancellationToken cancellationToken, bool startWorker = true)
    {
        IReadOnlyList<ChatMessageEntity> page;
        try
        {
            page = await _repo.ListMessagesPageDescAsync(_chat.Id, 0, 200, includePayloadBlob: false)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: could not load unsent messages after UDP fallback", _chat.Id);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var latestIds = new List<int>(UdpFallbackLatestUnsentCount);
        foreach (var row in page)
        {
            if (!row.Outgoing)
                continue;
            var status = (MessageDeliveryStatus)row.DeliveryStatus;
            if (status is not (MessageDeliveryStatus.Pending or MessageDeliveryStatus.Failed))
                continue;
            latestIds.Add(row.Id);
            if (latestIds.Count >= UdpFallbackLatestUnsentCount)
                break;
        }

        if (latestIds.Count == 0)
            return;

        latestIds.Reverse();
        foreach (var id in latestIds)
        {
            var row = page.First(r => r.Id == id);
            if ((MessageDeliveryStatus)row.DeliveryStatus == MessageDeliveryStatus.Failed)
            {
                try
                {
                    await _repo.UpdateMessageDeliveryStatusAsync(id, MessageDeliveryStatus.Pending)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Chat {ChatId}: could not requeue message {MessageId}", _chat.Id, id);
                }
            }

            lock (_pendingSync)
            {
                if (!_pendingOutgoing.Contains(id))
                    _pendingOutgoing.Add(id);
            }
        }

        RaiseMessagesChanged();
        HookPresenceForPendingFlush();
        if (startWorker)
            StartFlushPendingInBackground();
    }

    /// <summary>
    ///     Manual server ↔ mesh switch for this chat. The choice is stored on the chat row and blocks the
    ///     automatic UDP fallback until the user switches again. Crypto state is cleared and an invite is
    ///     resent so the peer re-establishes the session on the new path. A failed mesh connect is not
    ///     rolled back.
    /// </summary>
    public async Task SwitchDeliveryPathAsync(ChatDeliveryPath path, CancellationToken cancellationToken = default)
    {
        if (path is not (ChatDeliveryPath.Server or ChatDeliveryPath.Mesh))
            throw new ArgumentOutOfRangeException(nameof(path));

        if (path == ChatDeliveryPath.Server)
            await ForceServerPathAsync(cancellationToken).ConfigureAwait(false);
        else
            await ForceMeshPathAsync(cancellationToken).ConfigureAwait(false);

        RaiseDeliveryPathChanged();
    }

    private async Task ForceServerPathAsync(CancellationToken cancellationToken)
    {
        _deliveryPath = ChatDeliveryPath.Server;
        _udpFallbackApplied = 1;
        _udpOutboundUnavailable = true;
        await PersistDeliveryPathAsync().ConfigureAwait(false);
        _logger.LogInformation("Chat {ChatId}: manual switch to messenger server; reset session and re-invite",
            _chat.Id);

        await ResetCryptoStateAsync(cancellationToken).ConfigureAwait(false);
        // TryServerFallbackAsync pins a hosting server and republishes ChatRequest (server invite).
        var bound = await TryServerFallbackAsync(cancellationToken).ConfigureAwait(false);
        if (bound)
        {
            try
            {
                if (IsCryptoSessionLeader())
                {
                    await EnsureSessionAsInitiatorAsync(cancellationToken).ConfigureAwait(false);
                    _ = TryConfirmCryptoSessionAsync(cancellationToken);
                }
                else
                {
                    // ChatRequest from TryServerFallback notifies the leader; follower waits.
                    SetHandshakeStatus(ChatHandshakeStatus.InProgress);
                    try
                    {
                        await SendSessionSetupRequestPacketAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Chat {ChatId}: follower 0x04 after server-path switch failed",
                            _chat.Id);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Chat {ChatId}: server-path session renegotiation failed", _chat.Id);
            }
        }

        await EnqueueLatestUnsentMessagesAsync(cancellationToken).ConfigureAwait(false);
        if (!bound)
            throw new InvalidOperationException("Абонент не найден на доступных messenger-серверах.");
    }

    private async Task ForceMeshPathAsync(CancellationToken cancellationToken)
    {
        _deliveryPath = ChatDeliveryPath.Mesh;
        _udpFallbackApplied = 1;
        _udpOutboundUnavailable = false;
        _activeMessengerServerBaseUrl = null;
        _runtime.MessengerServers?.ClearPreferredServer(_chat.PeerNetworkIdShort);
        await PersistDeliveryPathAsync().ConfigureAwait(false);
        RebuildRouteFromChat();
        RaiseActiveChannelChanged();
        MergeAdditionalTransportPeerAddresses();
        PreferAdditionalTransportEndpoint();
        _logger.LogInformation("Chat {ChatId}: manual switch to mesh (UDP/BLE/LoRa); reset session and re-invite",
            _chat.Id);

        await ResetCryptoStateAsync(cancellationToken).ConfigureAwait(false);

        Exception? connectError = null;
        try
        {
            // Notify peer of path change; only leader drives handshake + test_send loop.
            await SendChatInviteWithRetryAsync(cancellationToken).ConfigureAwait(false);
            if (IsCryptoSessionLeader())
            {
                await EnsureSessionAsInitiatorAsync(cancellationToken).ConfigureAwait(false);
                _ = TryConfirmCryptoSessionAsync(cancellationToken);
            }
            else
            {
                SetHandshakeStatus(ChatHandshakeStatus.InProgress);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: mesh connect failed; delivery path stays mesh", _chat.Id);
            connectError = ex;
        }

        await EnqueueLatestUnsentMessagesAsync(cancellationToken).ConfigureAwait(false);
        if (connectError != null)
            throw connectError;
    }

    private async Task PersistDeliveryPathAsync()
    {
        _chat.DeliveryPath = (int)_deliveryPath;
        await _repo.UpdateChatDeliveryPathAsync(_chat.Id, _chat.DeliveryPath).ConfigureAwait(false);
    }

    private void AdoptStoredDeliveryPath()
    {
        _deliveryPath = _chat.DeliveryPath switch
        {
            (int)ChatDeliveryPath.Server => ChatDeliveryPath.Server,
            (int)ChatDeliveryPath.Mesh => ChatDeliveryPath.Mesh,
            _ => ChatDeliveryPath.Auto
        };
    }

    private void RaiseDeliveryPathChanged()
    {
        if (_uiSynchronizationContext != null)
            _uiSynchronizationContext.Post(_ => DeliveryPathChanged?.Invoke(this, EventArgs.Empty), null);
        else
            DeliveryPathChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseActiveChannelChanged()
    {
        if (_uiSynchronizationContext != null)
            _uiSynchronizationContext.Post(_ => ActiveChannelChanged?.Invoke(this, EventArgs.Empty), null);
        else
            ActiveChannelChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <returns>Server message id, or null to continue on the mesh path. Server mode throws instead of falling through.</returns>
    private async Task<string?> TryServerDeliveryAsync(byte[] wire, CancellationToken cancellationToken)
    {
        var servers = _runtime.MessengerServers;
        if (servers != null && MessengerServerSyncService.NeedsBotCatalogLookup(_chat))
        {
            var ensured = await servers
                .TryEnsureBotChatFromCatalogAsync(_chat.Id, cancellationToken)
                .ConfigureAwait(false);
            if (ensured != null)
                ApplyChatRow(ensured);
        }

        if (_chat.IsBot)
        {
            if (servers == null)
            {
                _logger.LogError("Chat {ChatId}: bot deliver — messenger servers not connected", _chat.Id);
                throw new InvalidOperationException("Messenger-серверы не подключены.");
            }

            _logger.LogDebug(
                "Chat {ChatId}: TryServerDelivery bot path peer={PeerId} wireLen={WireLen}",
                _chat.Id, _chat.PeerNetworkIdShort, wire.Length);
            var botAccepted = await servers.TryDeliverBotWireAsync(_chat, _user, wire, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(botAccepted))
            {
                _logger.LogDebug(
                    "Chat {ChatId}: bot deliver OK serverMessageId={ServerMessageId}",
                    _chat.Id, botAccepted);
                return botAccepted;
            }

            _logger.LogError(
                "Chat {ChatId}: bot deliver failed peer={PeerId}",
                _chat.Id, _chat.PeerNetworkIdShort);
            throw new InvalidOperationException("Не удалось доставить сообщение боту через messenger-сервер.");
        }

        if (_deliveryPath == ChatDeliveryPath.Mesh)
            return null;

        if (servers == null)
        {
            if (_deliveryPath == ChatDeliveryPath.Server)
                throw new InvalidOperationException("Messenger-серверы не подключены.");
            return null;
        }

        _logger.LogDebug(
            "Chat {ChatId}: TryServerDelivery human path peer={PeerId} wireLen={WireLen}",
            _chat.Id, _chat.PeerNetworkIdShort, wire.Length);
        var acceptedId = await servers.TryDeliverWireAsync(_chat, _user, wire, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrEmpty(acceptedId))
        {
            _logger.LogDebug(
                "Chat {ChatId}: human deliver OK serverMessageId={ServerMessageId}",
                _chat.Id, acceptedId);
            return acceptedId;
        }

        if (_deliveryPath == ChatDeliveryPath.Server)
            throw new InvalidOperationException("Абонент не найден на доступных messenger-серверах.");
        return null;
    }
}