using System.Buffers.Binary;
using System.Security.Cryptography;
using ShortP2P.Client.ChatMedia;
using ShortP2P.Crypto;

namespace ShortP2P.Client.Services;

/// <summary>
/// Compact binary session crypto probe for LoRa-friendly round-trips.
/// Wire (ChatWire): S2P1 + kind + len + body(version, probe-kind, challenge8).
/// After messenger chunk header (24) + AES-CBC+HMAC the encrypted radio packet is ≤
/// <see cref="MaxEncryptedBytes"/>.
/// Legacy text frames are still recognized on receive for older peers.
/// </summary>
public static class SessionCryptoProbe
{
    public const byte BodyVersion = 1;
    public const byte KindSend = 1;
    public const byte KindResponse = 2;
    public const int ChallengeBytes = 8;
    public const int BodyBytes = 1 + 1 + ChallengeBytes;

    /// <summary>Hard cap for one encrypted messenger chunk carrying a probe (send or response).</summary>
    public const int MaxEncryptedBytes = 80;

    /// <summary>Messenger <c>ChunkCodec</c> header size (Guid + index + total).</summary>
    public const int MessengerChunkHeaderBytes = 24;

    public const string KindTestSend = "test_send";
    public const string KindTestResponsePrefix = "test_response_for_";
    private const string TestSeparator = ":test:";

    public static byte[] CreateSendWire(out byte[] challenge)
    {
        challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
        return ChatWireCodec.EncodeSessionProbe(KindSend, challenge);
    }

    public static byte[] CreateResponseWire(ReadOnlySpan<byte> challenge) =>
        ChatWireCodec.EncodeSessionProbe(KindResponse, challenge);

    public static bool TryParseBinary(ChatWireSessionProbe probe, out SessionCryptoProbeKind kind,
        out byte[] challenge)
    {
        kind = default;
        challenge = Array.Empty<byte>();
        if (probe.Challenge.Length != ChallengeBytes)
            return false;
        if (probe.Kind == KindSend)
        {
            kind = SessionCryptoProbeKind.TestSend;
            challenge = (byte[])probe.Challenge.Clone();
            return true;
        }

        if (probe.Kind == KindResponse)
        {
            kind = SessionCryptoProbeKind.TestResponse;
            challenge = (byte[])probe.Challenge.Clone();
            return true;
        }

        return false;
    }

    public static string ChallengeToKey(ReadOnlySpan<byte> challenge) =>
        Convert.ToHexString(challenge).ToLowerInvariant();

    /// <summary>
    /// Encrypted size of one messenger chunk for a probe wire under current AES-CBC+HMAC.
    /// </summary>
    public static int EstimateEncryptedChunkBytes(int wireLength)
    {
        var plain = MessengerChunkHeaderBytes + wireLength;
        var rem = plain % 16;
        var padded = plain + (rem == 0 ? 16 : 16 - rem);
        return 16 + padded + 16;
    }

    public static void EnsureEncryptedSizeWithinBudget(byte[] wire)
    {
        Require.NotNull(wire);
        var encrypted = EstimateEncryptedChunkBytes(wire.Length);
        if (encrypted > MaxEncryptedBytes)
            throw new InvalidOperationException(
                $"Crypto probe encrypted size {encrypted} exceeds {MaxEncryptedBytes} bytes.");
    }

    /// <summary>Verifies budget with a real session encrypt (chunk header + wire).</summary>
    public static int MeasureEncryptedChunkBytes(P2PSession session, ReadOnlySpan<byte> wire)
    {
        Require.NotNull(session);
        var plain = new byte[MessengerChunkHeaderBytes + wire.Length];
        RandomNumberGenerator.Fill(plain.AsSpan(0, 16));
        BinaryPrimitives.WriteInt32BigEndian(plain.AsSpan(16), 0);
        BinaryPrimitives.WriteInt32BigEndian(plain.AsSpan(20), 1);
        wire.CopyTo(plain.AsSpan(MessengerChunkHeaderBytes));
        return session.Encrypt(plain).Length;
    }

    // --- Legacy text probes (receive-only compatibility) ---

    public static bool TryParse(string text, out SessionCryptoProbeMessage message)
    {
        message = default!;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();
        var sep = text.IndexOf(TestSeparator, StringComparison.Ordinal);
        if (sep <= 0)
            return false;

        var left = text[..sep];
        var right = text[(sep + TestSeparator.Length)..];
        if (left.Length == 0 || right.Length == 0)
            return false;

        var c1 = left.IndexOf(':');
        if (c1 <= 0)
            return false;
        var c2 = left.IndexOf(':', c1 + 1);
        if (c2 <= c1 + 1 || c2 >= left.Length - 1)
            return false;

        var src = left[..c1].Trim();
        var tgt = left[(c1 + 1)..c2].Trim();
        var kindToken = left[(c2 + 1)..].Trim();
        if (src.Length == 0 || tgt.Length == 0 || kindToken.Length == 0)
            return false;

        var lastColon = right.LastIndexOf(':');
        if (lastColon <= 0 || lastColon >= right.Length - 1)
            return false;

        var datetime = right[..lastColon].Trim();
        var hash = right[(lastColon + 1)..].Trim();
        if (datetime.Length == 0 || hash.Length == 0)
            return false;

        SessionCryptoProbeKind probeKind;
        string? referencedSendHash = null;
        if (string.Equals(kindToken, KindTestSend, StringComparison.Ordinal))
        {
            probeKind = SessionCryptoProbeKind.TestSend;
        }
        else if (kindToken.StartsWith(KindTestResponsePrefix, StringComparison.Ordinal) &&
                 kindToken.Length > KindTestResponsePrefix.Length)
        {
            probeKind = SessionCryptoProbeKind.TestResponse;
            referencedSendHash = kindToken[KindTestResponsePrefix.Length..];
        }
        else
            return false;

        var withoutHash = $"{src}:{tgt}:{kindToken}:test:{datetime}";
        var expectedHash = Convert.ToHexString(SHA512.HashData(System.Text.Encoding.UTF8.GetBytes(withoutHash)))
            .ToLowerInvariant();
        if (!FixedTimeEqualsHex(expectedHash, hash))
            return false;

        message = new SessionCryptoProbeMessage(probeKind, src, tgt, kindToken, datetime, hash, referencedSendHash);
        return true;
    }

    public static bool LooksLikeTestMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (TryParse(text, out _))
            return true;

        text = text.Trim();
        var sep = text.IndexOf(TestSeparator, StringComparison.Ordinal);
        if (sep <= 0)
            return false;
        var left = text[..sep];
        var c1 = left.IndexOf(':');
        if (c1 <= 0)
            return false;
        var c2 = left.IndexOf(':', c1 + 1);
        if (c2 <= c1 + 1 || c2 >= left.Length - 1)
            return false;
        var kind = left[(c2 + 1)..].Trim();
        return string.Equals(kind, KindTestSend, StringComparison.Ordinal) ||
               kind.StartsWith(KindTestResponsePrefix, StringComparison.Ordinal);
    }

    private static bool FixedTimeEqualsHex(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++)
            diff |= char.ToLowerInvariant(a[i]) ^ char.ToLowerInvariant(b[i]);
        return diff == 0;
    }
}

public enum SessionCryptoProbeKind
{
    TestSend,
    TestResponse
}

public sealed class SessionCryptoProbeMessage(
    SessionCryptoProbeKind kind,
    string sourceNetworkId,
    string targetNetworkId,
    string kindToken,
    string datetime,
    string contentHash,
    string? referencedTestSendHash)
{
    public SessionCryptoProbeKind Kind { get; } = kind;
    public string SourceNetworkId { get; } = sourceNetworkId;
    public string TargetNetworkId { get; } = targetNetworkId;
    public string KindToken { get; } = kindToken;
    public string DateTime { get; } = datetime;
    public string ContentHash { get; } = contentHash;
    public string? ReferencedTestSendHash { get; } = referencedTestSendHash;
}
