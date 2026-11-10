using System.Security.Cryptography;
using ShortP2P.Client.Services;

namespace ShortP2P.Client.Services.MessengerServers;

/// <summary>
/// In-memory messenger-server sessions, one outbound key per peer.
/// A session stays until its probe fails (no acknowledgement before <see cref="ProbeTimeout"/>,
/// or the peer asks to re-key). Process restart drops the keys; the next send establishes again.
/// </summary>
public sealed class ServerCipherSessionStore
{
    public static ServerCipherSessionStore Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, ServerCipherSession> _outbound = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServerCipherSession> _bySessionId = new(StringComparer.Ordinal);

    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Returns the outbound session for <paramref name="peerId"/>, creating one when missing
    /// and replacing it when the previous probe was not confirmed in time.
    /// </summary>
    public ServerCipherSession PrepareOutbound(string peerId, DateTimeOffset now, out bool establishedNow)
    {
        var id = Normalize(peerId);
        lock (_gate)
        {
            if (_outbound.TryGetValue(id, out var existing))
            {
                if (!existing.ProbeConfirmed && now - existing.EstablishedUtc >= ProbeTimeout)
                {
                    _bySessionId.Remove(IdKey(existing.SessionId));
                    existing = Create(now);
                    _outbound[id] = existing;
                    _bySessionId[IdKey(existing.SessionId)] = existing;
                    establishedNow = true;
                    return existing;
                }

                establishedNow = false;
                return existing;
            }

            var created = Create(now);
            _outbound[id] = created;
            _bySessionId[IdKey(created.SessionId)] = created;
            establishedNow = true;
            return created;
        }
    }

    /// <summary>
    /// Session that may encrypt a side payload (blob) without creating or rotating a key.
    /// Expired unconfirmed sessions are skipped so the next chat message can re-key.
    /// </summary>
    public bool TryGetUsableOutbound(string peerId, DateTimeOffset now, out ServerCipherSession? session)
    {
        var id = Normalize(peerId);
        lock (_gate)
        {
            if (!_outbound.TryGetValue(id, out var existing) ||
                (!existing.ProbeConfirmed && now - existing.EstablishedUtc >= ProbeTimeout))
            {
                session = null;
                return false;
            }

            session = existing;
            return true;
        }
    }

    public void RememberInbound(ReadOnlySpan<byte> sessionId, ReadOnlySpan<byte> key)
    {
        var id = sessionId.ToArray();
        var keyCopy = key.ToArray();
        lock (_gate)
        {
            var mapKey = IdKey(id);
            if (_bySessionId.ContainsKey(mapKey))
                return;
            _bySessionId[mapKey] = new ServerCipherSession(id, keyCopy, null, DateTimeOffset.UtcNow);
        }
    }

    public bool TryGetBySessionId(ReadOnlySpan<byte> sessionId, out ServerCipherSession? session)
    {
        lock (_gate)
        {
            return _bySessionId.TryGetValue(IdKey(sessionId), out session);
        }
    }

    public bool TryConfirmProbe(ReadOnlySpan<byte> sessionId, ReadOnlySpan<byte> token)
    {
        lock (_gate)
        {
            if (!_bySessionId.TryGetValue(IdKey(sessionId), out var session) ||
                session.OutstandingProbeToken == null ||
                session.OutstandingProbeToken.Length != token.Length ||
                !CryptographicOperations.FixedTimeEquals(session.OutstandingProbeToken, token))
            {
                return false;
            }

            session.ProbeConfirmed = true;
            return true;
        }
    }

    /// <summary>Drops our outbound key when the peer reports that this session id is unknown.</summary>
    public bool DropOutboundIfMatch(string peerId, ReadOnlySpan<byte> sessionId)
    {
        var id = Normalize(peerId);
        lock (_gate)
        {
            if (!_outbound.TryGetValue(id, out var session) ||
                !session.SessionId.AsSpan().SequenceEqual(sessionId))
            {
                return false;
            }

            _outbound.Remove(id);
            _bySessionId.Remove(IdKey(session.SessionId));
            return true;
        }
    }

    private static ServerCipherSession Create(DateTimeOffset now)
    {
        return new ServerCipherSession(
            Random(MessengerServerPayloadCodec.SessionIdBytes),
            Random(MessengerServerPayloadCodec.AesKeyBytes),
            Random(MessengerServerPayloadCodec.ProbeTokenBytes),
            now);
    }

    private static byte[] Random(int count)
    {
#if NET6_0_OR_GREATER
        return RandomNumberGenerator.GetBytes(count);
#else
        var bytes = new byte[count];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return bytes;
#endif
    }

    private static string Normalize(string peerId)
    {
        var id = ChatRepository.CanonicalPeerNetworkId(peerId);
        if (id.Length == 0)
            throw new ArgumentException("Peer network id is required.", nameof(peerId));
        return id;
    }

    private static string IdKey(ReadOnlySpan<byte> sessionId) => Convert.ToBase64String(sessionId.ToArray());
}
