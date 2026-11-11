namespace ShortP2P.Client.Services.MessengerServers;

/// <summary>
/// One direction of a messenger-server session. The sender creates it and wraps the key once
/// with the peer's RSA public key. Later payloads use only the symmetric key.
/// </summary>
public sealed class ServerCipherSession
{
    internal ServerCipherSession(byte[] sessionId, byte[] key, byte[]? probeToken, DateTimeOffset establishedUtc)
    {
        SessionId = sessionId;
        Key = key;
        OutstandingProbeToken = probeToken;
        EstablishedUtc = establishedUtc;
    }

    public byte[] SessionId { get; }

    public byte[] Key { get; }

    /// <summary>Peer proved it can decrypt this key (probe acknowledgement matched).</summary>
    public bool ProbeConfirmed { get; internal set; }

    public DateTimeOffset EstablishedUtc { get; internal set; }

    /// <summary>Token carried inside the RSA-wrapped establish frame. Null on a key learned from the peer.</summary>
    internal byte[]? OutstandingProbeToken { get; }
}
