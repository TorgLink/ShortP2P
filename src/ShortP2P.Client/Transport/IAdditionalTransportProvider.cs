using ShortP2P.Transport.Abstractions;

namespace ShortP2P.Client.Transport;

/// <summary>
///     Host-provided transports beyond UDP/Bluetooth (e.g. LoRa/Meshtastic Kind=5).
///     Discovery/presence UDP frames are not expected on these transports.
/// </summary>
public interface IAdditionalTransportProvider
{
    /// <summary>Whether this kind is currently enabled for TX/RX.</summary>
    bool IsEnabled(byte kind);

    /// <summary>Started (or ready-to-start) data-plane transports to multiplex into the data port.</summary>
    IReadOnlyList<ITransport> GetDataTransports();

    /// <summary>Resolve outbound for a destination, or null when this provider does not own the kind.</summary>
    ITransport? ResolveOutbound(TransportAddress destination);

    /// <summary>
    ///     After a cipher frame is attributed to a peer NetworkId, bind it for mesh directory updates
    ///     (e.g. Meshtastic REQ-DIR-3 step 4).
    /// </summary>
    void RememberPeer(byte[] networkIdWire12, TransportAddress from);

    /// <summary>
    ///     Whether a peer NetworkId (short form) was heard on this transport within its keep-alive
    ///     freshness window (e.g. mesh presence pings once per minute → online until two are missed).
    /// </summary>
    bool IsPeerRecentlySeen(string networkIdShort);

    /// <summary>
    ///     Known outbound addresses for a peer on this provider (e.g. Meshtastic MeshIds from the
    ///     directory). Empty when the peer is unknown or the transport is stopped.
    /// </summary>
    IReadOnlyList<TransportAddress> GetPeerAddresses(string networkIdShort);

    /// <summary>Raised with the peer NetworkId (short form) when a keep-alive/presence from it arrives.</summary>
    event EventHandler<string>? PeerPresenceSeen;
}
