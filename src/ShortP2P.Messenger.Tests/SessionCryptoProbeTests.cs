using ShortP2P.Client.ChatMedia;
using ShortP2P.Client.Services;
using ShortP2P.Crypto;

namespace ShortP2P.Messenger.Tests;

public class SessionCryptoProbeTests
{
    [Fact]
    public void CompactSendAndResponse_RoundTrip_AndStayWithin80EncryptedBytes()
    {
        var sendWire = SessionCryptoProbe.CreateSendWire(out var challenge);
        Assert.True(ChatWireCodec.TryParse(sendWire, out var sendParsed));
        var sendProbe = Assert.IsType<ChatWireSessionProbe>(sendParsed);
        Assert.True(SessionCryptoProbe.TryParseBinary(sendProbe, out var sendKind, out var sendChallenge));
        Assert.Equal(SessionCryptoProbeKind.TestSend, sendKind);
        Assert.Equal(challenge, sendChallenge);

        var responseWire = SessionCryptoProbe.CreateResponseWire(challenge);
        Assert.True(ChatWireCodec.TryParse(responseWire, out var respParsed));
        var respProbe = Assert.IsType<ChatWireSessionProbe>(respParsed);
        Assert.True(SessionCryptoProbe.TryParseBinary(respProbe, out var respKind, out var respChallenge));
        Assert.Equal(SessionCryptoProbeKind.TestResponse, respKind);
        Assert.Equal(challenge, respChallenge);

        SessionCryptoProbe.EnsureEncryptedSizeWithinBudget(sendWire);
        SessionCryptoProbe.EnsureEncryptedSizeWithinBudget(responseWire);
        Assert.True(SessionCryptoProbe.EstimateEncryptedChunkBytes(sendWire.Length) <= SessionCryptoProbe.MaxEncryptedBytes);
        Assert.True(SessionCryptoProbe.EstimateEncryptedChunkBytes(responseWire.Length) <= SessionCryptoProbe.MaxEncryptedBytes);

        var keys = P2PCrypto.GenerateKeyPair();
        var hs = P2PCrypto.CreateHandshakeInitiation(keys.PublicKey);
        var session = hs.Session;
        var sendEnc = SessionCryptoProbe.MeasureEncryptedChunkBytes(session, sendWire);
        var respEnc = SessionCryptoProbe.MeasureEncryptedChunkBytes(session, responseWire);
        Assert.True(sendEnc <= SessionCryptoProbe.MaxEncryptedBytes, $"send encrypted {sendEnc}");
        Assert.True(respEnc <= SessionCryptoProbe.MaxEncryptedBytes, $"response encrypted {respEnc}");
        Assert.Equal(sendWire.Length, responseWire.Length);
    }

    [Fact]
    public void LegacyTextProbe_StillParsedForCompatibility()
    {
        // Rebuild a valid legacy frame the way older clients did.
        var src = "nidA";
        var tgt = "nidB";
        var datetime = "2026-10-06T12:00:00.000Z";
        var withoutHash = $"{src}:{tgt}:test_send:test:{datetime}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(
            System.Text.Encoding.UTF8.GetBytes(withoutHash))).ToLowerInvariant();
        var frame = withoutHash + ":" + hash;

        Assert.True(SessionCryptoProbe.TryParse(frame, out var msg));
        Assert.Equal(SessionCryptoProbeKind.TestSend, msg.Kind);
        Assert.True(SessionCryptoProbe.LooksLikeTestMessage(frame));
        Assert.False(SessionCryptoProbe.LooksLikeTestMessage("hello chat"));
    }
}
