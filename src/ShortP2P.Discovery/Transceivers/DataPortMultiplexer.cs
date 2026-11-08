using ShortP2P.Auth.Data;
using ShortP2P.Transport;
using ShortP2P.Transport.Abstractions;

namespace ShortP2P.Discovery.Transceivers;

/// <summary>
///     Делит общий приём на data-порту (handshake/cipher) между <see cref="HandshakeTransceiver" /> и
///     <see cref="MessageTransceiver" /> по первому байту датаграммы. Один UDP-сокет (плюс опциональный
///     Bluetooth) — две точки приёма. Broadcast-датаграммы отбрасываются до раздачи.
/// </summary>
public sealed class DataPortMultiplexer : IAsyncDisposable
{
    private readonly Func<TransportAddress, ITransport?> _outboundResolver;
    private readonly Func<TransportAddress, bool>? _shouldAcceptFrom;

    public DataPortMultiplexer(Func<TransportAddress, ITransport?> outboundResolver,
        Func<TransportAddress, bool>? shouldAcceptFrom = null)
    {
        _outboundResolver = outboundResolver ?? throw new ArgumentNullException(nameof(outboundResolver));
        _shouldAcceptFrom = shouldAcceptFrom;

        Handshake = new HandshakeTransceiver(SendRawAsync);
        Message = new MessageTransceiver(SendRawAsync);
        BleNetworkId = new BleNetworkIdTransceiver(SendRawAsync);
    }

    public HandshakeTransceiver Handshake { get; }

    public MessageTransceiver Message { get; }

    public BleNetworkIdTransceiver BleNetworkId { get; }

    public ValueTask DisposeAsync()
    {
        return StopAsync();
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await Handshake.StartAsync(cancellationToken).ConfigureAwait(false);
        await Message.StartAsync(cancellationToken).ConfigureAwait(false);
        await BleNetworkId.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await Handshake.StopAsync(cancellationToken).ConfigureAwait(false);
        await Message.StopAsync(cancellationToken).ConfigureAwait(false);
        await BleNetworkId.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendRawAsync(ReadOnlyMemory<byte> packet, TransportAddress destination,
        CancellationToken cancellationToken)
    {
        if (BroadcastAddressFilter.IsLocalIpv4Broadcast(destination))
            throw new InvalidOperationException("Broadcast is not allowed on data port (handshake/cipher).");
        var transport = _outboundResolver(destination)
                        ?? throw new InvalidOperationException(
                            $"No outbound transport for destination kind {destination.Kind}.");
        await transport.SendAsync(packet, destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Передаёт входящую datagram data-порта в multiplexer. Вызывается внешним pump/owner.
    /// </summary>
    public void HandleIncoming(TransportReceiveMessage msg)
    {
        if (msg.Payload.IsEmpty)
            return;
        if (BroadcastAddressFilter.IsLocalIpv4Broadcast(msg.RemoteAddress))
            return;
        if (_shouldAcceptFrom != null && !_shouldAcceptFrom(msg.RemoteAddress))
            return;

        if (BleNetworkIdPacketCodec.TryParsePacket(msg.Payload.Span, out _))
        {
            BleNetworkId.HandleIncoming(msg.Payload, msg.RemoteAddress);
            return;
        }

        var first = msg.Payload.Span[0];
        switch (first)
        {
            case (byte)HandshakeKind.Handshake when msg.Payload.Length == 129:
                Handshake.HandleIncoming(HandshakeKind.Handshake, msg.Payload.Slice(1), msg.RemoteAddress);
                return;
            // 0x04 + CompressedNetworkId wire bytes (12). Hardcoded 17 was left over from Guid-sized ids.
            case (byte)HandshakeKind.SessionSetupRequest
                when msg.Payload.Length == 1 + CompressedNetworkId.WireLength:
                Handshake.HandleIncoming(HandshakeKind.SessionSetupRequest, msg.Payload.Slice(1), msg.RemoteAddress);
                return;
            case MessageTransceiver.FrameCipher when msg.Payload.Length > 1:
                Message.HandleIncoming(msg.Payload.Slice(1), msg.RemoteAddress);
                return;
        }
    }
}