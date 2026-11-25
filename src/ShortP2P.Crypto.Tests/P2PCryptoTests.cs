using System.Security.Cryptography;

namespace ShortP2P.Crypto.Tests;

public class P2PCryptoTests
{
    [Fact]
    public void GenerateKeyPair_ShouldReturnValidKeys()
    {
        var kp = P2PCrypto.GenerateKeyPair();

        Assert.NotNull(kp);
        Assert.Equal(IdentityPublicKey.Ed25519PublicKeyLength, kp.PublicKey.Ed25519Public.Length);
        Assert.Equal(IdentityPublicKey.X25519PublicKeyLength, kp.PublicKey.X25519Public.Length);
        Assert.Equal(IdentityPrivateKey.Ed25519SeedLength, kp.PrivateKey.Ed25519Seed.Length);
        Assert.Equal(IdentityPrivateKey.X25519PrivateKeyLength, kp.PrivateKey.X25519Private.Length);
    }

    [Fact]
    public void NetworkId_ShouldBindToEd25519PublicKey()
    {
        var kp = P2PCrypto.GenerateKeyPair();
        var wire = IdentityKeyDerivation.NetworkIdWireFromPublicKey(kp.PublicKey);
        Assert.Equal(12, wire.Length);
        Assert.True(IdentityKeyDerivation.NetworkIdMatchesPublicKey(wire, kp.PublicKey));

        var other = P2PCrypto.GenerateKeyPair();
        Assert.False(IdentityKeyDerivation.NetworkIdMatchesPublicKey(wire, other.PublicKey));
    }

    [Fact]
    public void EncryptDecrypt_ShouldRoundTrip_ForDifferentPlaintextSizes()
    {
        var bobKeys = P2PCrypto.GenerateKeyPair();

        var hs = P2PCrypto.CreateHandshakeInitiation(bobKeys.PublicKey);
        var aliceSession = hs.Session;
        var bobSession = P2PCrypto.CreateSession(bobKeys.PrivateKey, hs.HandshakePacket);

        Assert.Equal(P2PHandshake.HandshakePacketBytes, hs.HandshakePacket.Length);
        Assert.NotNull(aliceSession);
        Assert.NotNull(bobSession);

        var sizes = new List<int> { 0, 1, 15, aliceSession.MaxPlaintextBytes };
        foreach (var size in sizes)
        {
            var plaintext = RandomBytes(size);
            var encrypted = aliceSession.Encrypt(plaintext);
            Assert.True(encrypted.Length <= 1024, $"Encrypted packet len={encrypted.Length}, plaintext len={size}");

            var decrypted = bobSession.Decrypt(encrypted);
            Assert.True(plaintext.SequenceEqual(decrypted), $"Mismatch at plaintext len={size}");
        }
    }

    [Fact]
    public void Decrypt_TamperedPacket_ShouldThrowCryptographicException()
    {
        var bobKeys = P2PCrypto.GenerateKeyPair();

        var hs = P2PCrypto.CreateHandshakeInitiation(bobKeys.PublicKey);
        var session = P2PCrypto.CreateSession(bobKeys.PrivateKey, hs.HandshakePacket);

        var plaintext = RandomBytes(32);
        var encrypted = hs.Session.Encrypt(plaintext);

        Assert.True(encrypted.Length <= 1024);

        var tampered = (byte[])encrypted.Clone();
        tampered[^1] ^= 0x01;

        Assert.Throws<CryptographicException>(() => session.Decrypt(tampered));
    }

    [Fact]
    public void Encrypt_TooLargePlaintext_ShouldThrowArgumentException()
    {
        var bobKeys = P2PCrypto.GenerateKeyPair();

        var hs = P2PCrypto.CreateHandshakeInitiation(bobKeys.PublicKey);
        var session = hs.Session;

        var tooLarge = new byte[session.MaxPlaintextBytes + 1];

        Assert.Throws<ArgumentException>(() => session.Encrypt(tooLarge));
    }

    private static byte[] RandomBytes(int len)
    {
        switch (len)
        {
            case < 0:
                throw new ArgumentOutOfRangeException(nameof(len));
            case 0:
                return [];
        }

        var data = new byte[len];
        RandomNumberGenerator.Fill(data);
        return data;
    }
}
