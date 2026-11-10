using ShortP2P.Client.Services.MessengerServers;
using ShortP2P.Crypto;

namespace ShortP2P.Messenger.Tests;

public class ServerCipherSessionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Open_LegacyEnvelope_Roundtrip()
    {
        var keys = P2PCrypto.GenerateKeyPair();
        var plain = "legacy"u8.ToArray();
        var envelope = MessengerServerPayloadCodec.Encrypt(plain, keys.PublicKey);

        var opened = MessengerServerPayloadCodec.Open(
            envelope, keys.PrivateKey, "peer", new ServerCipherSessionStore());

        Assert.Equal(plain, opened.ChatWire);
        Assert.Null(opened.ResponseEnvelope);
        Assert.True(opened.ShouldPersistChat);
    }

    [Fact]
    public void Session_FirstMessageWrapsKey_LaterMessagesAreSymmetric()
    {
        var bob = P2PCrypto.GenerateKeyPair();
        var aliceStore = new ServerCipherSessionStore();
        var bobStore = new ServerCipherSessionStore();
        var firstPlain = "hello"u8.ToArray();

        var establish = MessengerServerPayloadCodec.EncryptForSession(
            firstPlain, bob.PublicKey, "bob", aliceStore, T0);
        Assert.Equal(2, establish[0]);
        Assert.Equal(1, establish[1]);

        var opened = MessengerServerPayloadCodec.Open(establish, bob.PrivateKey, "alice", bobStore);
        Assert.Equal(firstPlain, opened.ChatWire);
        Assert.NotNull(opened.ResponseEnvelope);
        Assert.Equal(4, opened.ResponseEnvelope![1]);

        var secondPlain = "again"u8.ToArray();
        var data = MessengerServerPayloadCodec.EncryptForSession(
            secondPlain, bob.PublicKey, "bob", aliceStore, T0.AddSeconds(5));
        Assert.Equal(2, data[1]);
        Assert.True(data.Length < establish.Length);

        var second = MessengerServerPayloadCodec.Open(data, bob.PrivateKey, "alice", bobStore);
        Assert.Equal(secondPlain, second.ChatWire);
        Assert.Null(second.ResponseEnvelope);
    }

    [Fact]
    public void ProbeAck_ConfirmsSession_SoTimeoutDoesNotRekey()
    {
        var bob = P2PCrypto.GenerateKeyPair();
        var alice = P2PCrypto.GenerateKeyPair();
        var aliceStore = new ServerCipherSessionStore();
        var bobStore = new ServerCipherSessionStore();

        var establish = MessengerServerPayloadCodec.EncryptForSession(
            "m1"u8.ToArray(), bob.PublicKey, "bob", aliceStore, T0);
        var opened = MessengerServerPayloadCodec.Open(establish, bob.PrivateKey, "alice", bobStore);
        var ack = MessengerServerPayloadCodec.Open(
            opened.ResponseEnvelope!, alice.PrivateKey, "bob", aliceStore);
        Assert.False(ack.ShouldPersistChat);

        var before = MessengerServerPayloadCodec.EncryptForSession(
            "m2"u8.ToArray(), bob.PublicKey, "bob", aliceStore, T0.AddMinutes(30));
        Assert.Equal(2, before[1]);

        var after = MessengerServerPayloadCodec.EncryptForSession(
            "m3"u8.ToArray(), bob.PublicKey, "bob", aliceStore, T0.AddHours(5));
        Assert.Equal(2, after[1]);
        Assert.Equal(before.AsSpan(2, 16).ToArray(), after.AsSpan(2, 16).ToArray());
    }

    [Fact]
    public void ProbeTimeout_NextSendWrapsANewKey()
    {
        var bob = P2PCrypto.GenerateKeyPair();
        var aliceStore = new ServerCipherSessionStore { ProbeTimeout = TimeSpan.FromSeconds(60) };

        MessengerServerPayloadCodec.EncryptForSession(
            "m1"u8.ToArray(), bob.PublicKey, "bob", aliceStore, T0);
        var data = MessengerServerPayloadCodec.EncryptForSession(
            "m2"u8.ToArray(), bob.PublicKey, "bob", aliceStore, T0.AddSeconds(10));
        Assert.Equal(2, data[1]);

        var rekeyed = MessengerServerPayloadCodec.EncryptForSession(
            "m3"u8.ToArray(), bob.PublicKey, "bob", aliceStore, T0.AddSeconds(61));
        Assert.Equal(1, rekeyed[1]);

        var dataAfter = MessengerServerPayloadCodec.EncryptForSession(
            "m4"u8.ToArray(), bob.PublicKey, "bob", aliceStore, T0.AddSeconds(62));
        Assert.Equal(2, dataAfter[1]);
        Assert.NotEqual(data.AsSpan(2, 16).ToArray(), dataAfter.AsSpan(2, 16).ToArray());
    }

    [Fact]
    public void UnknownSession_AsksForRekey_AndSenderWrapsAgain()
    {
        var bob = P2PCrypto.GenerateKeyPair();
        var alice = P2PCrypto.GenerateKeyPair();
        var aliceStore = new ServerCipherSessionStore();
        var bobStore = new ServerCipherSessionStore();

        var establish = MessengerServerPayloadCodec.EncryptForSession(
            "m1"u8.ToArray(), bob.PublicKey, "bob", aliceStore, T0);
        var opened = MessengerServerPayloadCodec.Open(establish, bob.PrivateKey, "alice", bobStore);
        MessengerServerPayloadCodec.Open(opened.ResponseEnvelope!, alice.PrivateKey, "bob", aliceStore);

        var data = MessengerServerPayloadCodec.EncryptForSession(
            "m2"u8.ToArray(), bob.PublicKey, "bob", aliceStore, T0.AddSeconds(1));
        var lost = MessengerServerPayloadCodec.Open(
            data, bob.PrivateKey, "alice", new ServerCipherSessionStore());
        Assert.False(lost.ShouldPersistChat);
        Assert.NotNull(lost.ResponseEnvelope);
        Assert.Equal(5, lost.ResponseEnvelope![1]);

        var notice = MessengerServerPayloadCodec.Open(
            lost.ResponseEnvelope, alice.PrivateKey, "bob", aliceStore);
        Assert.False(notice.ShouldPersistChat);

        var again = MessengerServerPayloadCodec.EncryptForSession(
            "m3"u8.ToArray(), bob.PublicKey, "bob", aliceStore, T0.AddSeconds(2));
        Assert.Equal(1, again[1]);
    }

    [Fact]
    public void EachDirection_WrapsItsOwnKeyOnce()
    {
        var alice = P2PCrypto.GenerateKeyPair();
        var bob = P2PCrypto.GenerateKeyPair();
        var aliceStore = new ServerCipherSessionStore();
        var bobStore = new ServerCipherSessionStore();

        var aToB = MessengerServerPayloadCodec.EncryptForSession(
            "from-a"u8.ToArray(), bob.PublicKey, "bob", aliceStore, T0);
        Assert.Equal(1, aToB[1]);
        MessengerServerPayloadCodec.Open(aToB, bob.PrivateKey, "alice", bobStore);

        var bToA = MessengerServerPayloadCodec.EncryptForSession(
            "from-b"u8.ToArray(), alice.PublicKey, "alice", bobStore, T0);
        Assert.Equal(1, bToA[1]);

        var bAgain = MessengerServerPayloadCodec.EncryptForSession(
            "from-b-2"u8.ToArray(), alice.PublicKey, "alice", bobStore, T0.AddSeconds(1));
        Assert.Equal(2, bAgain[1]);
    }

    [Fact]
    public void Blob_UsesLegacyWrapUntilSessionExists_ThenSymmetric()
    {
        var bob = P2PCrypto.GenerateKeyPair();
        var store = new ServerCipherSessionStore();
        var blob = "blob"u8.ToArray();

        var before = MessengerServerPayloadCodec.EncryptPreferSession(
            blob, bob.PublicKey, "bob", store, T0);
        Assert.Equal(1, before[0]);

        MessengerServerPayloadCodec.EncryptForSession("hi"u8.ToArray(), bob.PublicKey, "bob", store, T0);
        var after = MessengerServerPayloadCodec.EncryptPreferSession(
            blob, bob.PublicKey, "bob", store, T0.AddSeconds(1));
        Assert.Equal(2, after[0]);
        Assert.Equal(2, after[1]);
    }

    [Fact]
    public void TamperedSymmetricPayload_Throws()
    {
        var bob = P2PCrypto.GenerateKeyPair();
        var aliceStore = new ServerCipherSessionStore();
        var bobStore = new ServerCipherSessionStore();
        var establish = MessengerServerPayloadCodec.EncryptForSession(
            "m"u8.ToArray(), bob.PublicKey, "bob", aliceStore, T0);
        MessengerServerPayloadCodec.Open(establish, bob.PrivateKey, "alice", bobStore);
        var data = MessengerServerPayloadCodec.EncryptForSession(
            "body"u8.ToArray(), bob.PublicKey, "bob", aliceStore, T0.AddSeconds(1));
        data[^1] ^= 0xFF;

        var ex = Record.Exception(() =>
            MessengerServerPayloadCodec.Open(data, bob.PrivateKey, "alice", bobStore));
        Assert.IsAssignableFrom<System.Security.Cryptography.CryptographicException>(ex);
    }
}
