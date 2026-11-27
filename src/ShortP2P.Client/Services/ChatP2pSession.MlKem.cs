using Microsoft.Extensions.Logging;
using ShortP2P.Auth.Data;
using ShortP2P.Client.ChatMedia;
using ShortP2P.Crypto;
using ShortP2P.Discovery.Transceivers;
using ShortP2P.Transport.Abstractions;

namespace ShortP2P.Client.Services;

public sealed partial class ChatP2PSession
{
    private const byte MlKemFlagWants = 0x01;

    private int _peerAdvertisesMlKem;
    private int _isMlKemSession;
    private int _mlKemAdvertiseInFlight;
    private int _mlKemReestablishInFlight;
    private int _mlKemReestablishAfterMutual;

    /// <summary>Raised when <see cref="IsMlKemSession"/> changes (UI quantum mark).</summary>
    public event EventHandler? MlKemSessionChanged;

    /// <summary>Peer advertised EnableMlKem via capability exchange.</summary>
    public bool PeerAdvertisesMlKem => Volatile.Read(ref _peerAdvertisesMlKem) == 1;

    /// <summary>
    /// Both sides want ML-KEM and verification completed after capability exchange
    /// (and automatic re-establish when upgrading from a classic session).
    /// </summary>
    public bool IsMlKemSession => Volatile.Read(ref _isMlKemSession) == 1;

    private bool LocalWantsMlKem =>
        (_routingSettings?.EnableMlKem ?? true) && MlKemCrypto.IsFeatureEnabled && MlKemCrypto.IsSupported;

    /// <summary>Called when the user toggles EnableMlKem in settings.</summary>
    public Task OnLocalMlKemSettingChangedAsync()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        return AdvertiseAndEvaluateMlKemAsync(token);
    }

    /// <summary>
    /// Apply peer ML-KEM capability from messenger-server inbox (may arrive before
    /// <see cref="IsReadyForServerReceive"/> / handshake Established).
    /// </summary>
    public Task ApplyPeerMlKemCapabilityFromServerAsync(bool wantsMlKem, CancellationToken cancellationToken) =>
        ApplyPeerMlKemAdvertisementAsync(wantsMlKem, cancellationToken);

    private void RaiseMlKemSessionChanged()
    {
        if (_uiSynchronizationContext != null)
            _uiSynchronizationContext.Post(_ => MlKemSessionChanged?.Invoke(this, EventArgs.Empty), null);
        else
            MlKemSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetMlKemSession(bool value)
    {
        var next = value ? 1 : 0;
        if (Interlocked.Exchange(ref _isMlKemSession, next) == next)
            return;
        _chat.MlKemSessionVerified = value;
        _ = PersistMlKemVerifiedAsync(value);
        RaiseMlKemSessionChanged();
        LogSessionEncryptionType(value
            ? "session encryption switched to ML-KEM"
            : "session encryption switched to legacy");
    }

    /// <summary>
    /// Information log of the negotiated session crypto type (legacy vs ML-KEM / quantum)
    /// plus cryptographic strength parameters from handshake / session setup.
    /// Reflects mutual capability verification; AES key material from ML-KEM remains Phase B.
    /// </summary>
    private void LogSessionEncryptionType(string eventName)
    {
        // Prefer live mutual-verify flag; fall back to persisted mark for restarted chats.
        var mlKem = IsMlKemSession || _chat.MlKemSessionVerified;
        var encryption = mlKem ? "ML-KEM" : "legacy";
        var cryptoParams = P2PCrypto.DescribeSessionParameters(mlKem);
        _logger.LogInformation(
            "Chat {ChatId}: {Event}; encryption={Encryption} (peerAdvertisesMlKem={Peer}, localWantsMlKem={Local}, handshake={Handshake}); cryptoParams={CryptoParams}",
            _chat.Id, eventName, encryption, PeerAdvertisesMlKem, LocalWantsMlKem, _handshakeStatus,
            cryptoParams);
    }

    private async Task PersistMlKemVerifiedAsync(bool verified)
    {
        try
        {
            await _repo.TrySetMlKemSessionVerifiedAsync(_chat.Id, verified).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Chat {ChatId}: persist MlKemSessionVerified failed", _chat.Id);
        }
    }

    private void OnHandshakeEstablishedMaybeAdvertiseMlKem()
    {
        if (_chat.MlKemSessionVerified)
            SetMlKemSession(true);
        var token = _cts?.Token ?? CancellationToken.None;
        _ = AdvertiseAndEvaluateMlKemAsync(token);
    }

    private bool IsCryptoPathReadyForMlKem() =>
        _handshakeStatus == ChatHandshakeStatus.Established
        || IsFollowerCryptoReady()
        || (_messenger != null && TryGetCryptoSession(out _));

    private async Task AdvertiseAndEvaluateMlKemAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _mlKemAdvertiseInFlight, 1, 0) != 0)
            return;
        try
        {
            await SendMlKemCapabilityAsync(cancellationToken).ConfigureAwait(false);
            await EvaluateMutualMlKemAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Chat {ChatId}: ML-KEM advertise/evaluate failed", _chat.Id);
        }
        finally
        {
            Interlocked.Exchange(ref _mlKemAdvertiseInFlight, 0);
        }
    }

    private async Task SendMlKemCapabilityAsync(CancellationToken cancellationToken)
    {
        var wants = LocalWantsMlKem;
        var delivered = false;

        try
        {
            await SendRawMlKemCapabilityFrameAsync(wants, cancellationToken).ConfigureAwait(false);
            delivered = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Chat {ChatId}: raw ML-KEM capability frame (0x05) failed", _chat.Id);
        }

        // P2P AES messenger path (needs completed identity handshake).
        if (_messenger != null && TryGetCryptoSession(out _))
        {
            try
            {
                var wire = ChatWireCodec.EncodeMlKemCapability(wants);
                await DeliverControlWireAsync(wire, cancellationToken).ConfigureAwait(false);
                delivered = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Chat {ChatId}: AES ML-KEM capability send failed", _chat.Id);
            }
        }

        // Messenger-server path: same identity-encrypted ChatWire as normal messages.
        // Required when UDP is down and P2P AES session is not ready yet (common follower case).
        try
        {
            var wire = ChatWireCodec.EncodeMlKemCapability(wants);
            var serverMessageId = await TryServerDeliveryAsync(wire, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(serverMessageId))
            {
                delivered = true;
                _logger.LogInformation(
                    "Chat {ChatId}: ML-KEM capability sent via messenger server wants={Wants} serverMessageId={ServerMessageId}",
                    _chat.Id, wants, serverMessageId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Chat {ChatId}: server ML-KEM capability send failed", _chat.Id);
        }

        if (!delivered)
        {
            _logger.LogInformation(
                "Chat {ChatId}: ML-KEM capability not delivered yet wants={Wants} (peerAdvertisesMlKem still {Peer})",
                _chat.Id, wants, PeerAdvertisesMlKem);
        }
    }

    private async Task SendRawMlKemCapabilityFrameAsync(bool wants, CancellationToken cancellationToken)
    {
        var id = CompressedNetworkId.FromShortString(_user.NetworkIdShort.Trim());
        var buf = new byte[1 + CompressedNetworkId.WireLength + 1];
        buf[0] = (byte)HandshakeKind.MlKemCapability;
        if (!id.TryWriteBytes(buf.AsSpan(1, CompressedNetworkId.WireLength)))
            throw new InvalidOperationException("Failed to write network id for ML-KEM capability.");
        buf[1 + CompressedNetworkId.WireLength] = wants ? MlKemFlagWants : (byte)0;
        await SendRouteRawAsync(buf, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessMlKemCapabilityFrameAsync(ReadOnlyMemory<byte> body, TransportAddress remoteAddress,
        CancellationToken cancellationToken)
    {
        if (body.Length != CompressedNetworkId.WireLength + 1)
        {
            _logger.LogDebug(
                "Chat {ChatId}: ignored ML-KEM capability from {Remote}, invalid length {Length}",
                _chat.Id, FormatTransportAddress(remoteAddress), body.Length);
            return;
        }

        var peerId = CompressedNetworkId.FromWireBytes(body.Span[..CompressedNetworkId.WireLength]);
        var expected = CompressedNetworkId.FromShortString(_chat.PeerNetworkIdShort.Trim());
        if (peerId != expected)
        {
            _logger.LogDebug(
                "Chat {ChatId}: ignored ML-KEM capability from {Remote}, id mismatch",
                _chat.Id, FormatTransportAddress(remoteAddress));
            return;
        }

        PreferEndpoint(remoteAddress);
        var wants = (body.Span[CompressedNetworkId.WireLength] & MlKemFlagWants) != 0;
        await ApplyPeerMlKemAdvertisementAsync(wants, cancellationToken).ConfigureAwait(false);
    }

    private Task ApplyPeerMlKemAdvertisementFromWireAsync(ChatWireMlKemCapability cap,
        CancellationToken cancellationToken) =>
        ApplyPeerMlKemAdvertisementAsync(cap.WantsMlKem, cancellationToken);

    private async Task ApplyPeerMlKemAdvertisementAsync(bool wants, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _peerAdvertisesMlKem, wants ? 1 : 0);
        _logger.LogInformation(
            "Chat {ChatId}: peer ML-KEM capability wants={Wants} (localWants={Local})",
            _chat.Id, wants, LocalWantsMlKem);
        await EvaluateMutualMlKemAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EvaluateMutualMlKemAsync(CancellationToken cancellationToken)
    {
        var mutual = LocalWantsMlKem && PeerAdvertisesMlKem;
        if (!mutual)
        {
            if (IsMlKemSession)
                SetMlKemSession(false);
            Interlocked.Exchange(ref _mlKemReestablishAfterMutual, 0);
            return;
        }

        if (!IsCryptoPathReadyForMlKem())
            return;

        // Both sides want ML-KEM. If we are not yet on a verified ML-KEM session, re-establish once.
        if (!IsMlKemSession)
        {
            if (Interlocked.CompareExchange(ref _mlKemReestablishAfterMutual, 1, 0) == 0)
            {
                await ReestablishSessionForMlKemAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (mutual && IsCryptoPathReadyForMlKem())
            SetMlKemSession(true);
    }

    private async Task ReestablishSessionForMlKemAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _mlKemReestablishInFlight, 1, 0) != 0)
            return;
        try
        {
            _logger.LogInformation(
                "Chat {ChatId}: both peers want ML-KEM — re-establishing session for quantum path",
                _chat.Id);
            ClearCryptoSession();
            SetHandshakeStatus(ChatHandshakeStatus.InProgress);
            // Fresh start path: StartAsync is gated; use EnsureSessionAsInitiator + probe/nudge.
            try
            {
                await EnsureSessionAsInitiatorAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Chat {ChatId}: ML-KEM re-establish session setup failed", _chat.Id);
            }

            if (IsCryptoSessionLeader())
                _ = TryConfirmCryptoSessionAsync(cancellationToken);
            else
                _ = NudgeLeaderSessionSetupAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref _mlKemReestablishInFlight, 0);
        }
    }
}
