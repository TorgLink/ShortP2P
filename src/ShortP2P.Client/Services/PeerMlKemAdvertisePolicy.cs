namespace ShortP2P.Client.Services;

/// <summary>
/// Decides whether an inbound peer ML-KEM capability frame should update local state.
/// Server inbox can deliver <c>wants=False</c> out of order after a newer <c>wants=True</c>
/// (e.g. after a brief EnableMlKem toggle), which used to clear the quantum mark.
/// </summary>
public static class PeerMlKemAdvertisePolicy
{
    /// <summary>
    /// Ignore <c>wants=False</c> for this long after latching True when there is no live P2P AES path.
    /// </summary>
    public static readonly TimeSpan StaleFalseGrace = TimeSpan.FromSeconds(30);

    /// <param name="wants">Peer capability flag from the wire.</param>
    /// <param name="currentlyAdvertises">Local latched peer-advertise state.</param>
    /// <param name="liveP2pCryptoPath">True when P2P AES session / Established path is ready.</param>
    /// <param name="utcNow">Clock for grace window.</param>
    /// <param name="lastTrueUtc">When we last applied wants=True (UTC); default if never.</param>
    public static bool ShouldApply(
        bool wants,
        bool currentlyAdvertises,
        bool liveP2pCryptoPath,
        DateTimeOffset utcNow,
        DateTimeOffset lastTrueUtc)
    {
        if (wants)
            return true;

        // No latched True yet — ignore False noise / default frames.
        if (!currentlyAdvertises)
            return false;

        // Live P2P AES path: treat False as authoritative (peer turned ML-KEM off).
        if (liveP2pCryptoPath)
            return true;

        // Server-only / handshake still InProgress: suppress False that arrives soon after True
        // (typical out-of-order or race after a settings toggle).
        if (lastTrueUtc != default && utcNow - lastTrueUtc < StaleFalseGrace)
            return false;

        // True was latched long enough ago — allow peer to downgrade over the server path.
        return true;
    }
}
