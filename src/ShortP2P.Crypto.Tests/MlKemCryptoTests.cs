namespace ShortP2P.Crypto.Tests;

public class MlKemCryptoTests
{
    [Theory]
    [InlineData(MlKemParameterSet.MlKem512)]
    [InlineData(MlKemParameterSet.MlKem768)]
    [InlineData(MlKemParameterSet.MlKem1024)]
    public void GenerateEncapsulateDecapsulate_ShouldAgreeOnSharedSecret(MlKemParameterSet parameterSet)
    {
        Assert.True(MlKemCrypto.IsSupported);

        var alice = MlKemCrypto.GenerateKeyPair(parameterSet);
        Assert.Equal(MlKemCrypto.PublicKeyByteLength(parameterSet), alice.PublicKey.Encoded.Length);
        Assert.Equal(MlKemCrypto.PrivateKeyByteLength(parameterSet), alice.PrivateKey.Encoded.Length);

        var bob = MlKemCrypto.Encapsulate(alice.PublicKey);
        Assert.Equal(MlKemCrypto.CiphertextByteLength(parameterSet), bob.Ciphertext.Length);
        Assert.Equal(MlKemCrypto.SharedSecretByteLength, bob.SharedSecret.Length);

        var aliceSecret = MlKemCrypto.Decapsulate(alice.PrivateKey, bob.Ciphertext);
        Assert.Equal(bob.SharedSecret, aliceSecret);
    }

    [Fact]
    public void DefaultParameterSet_ShouldBeMlKem768()
    {
        Assert.Equal(MlKemParameterSet.MlKem768, MlKemCrypto.DefaultParameterSet);
        Assert.Equal("ML-KEM", MlKemCrypto.CapabilityName);
    }

    [Fact]
    public void ImportPublicAndPrivate_ShouldRoundTrip()
    {
        var original = MlKemCrypto.GenerateKeyPair(MlKemParameterSet.MlKem768);
        var publicKey = MlKemCrypto.ImportPublicKey(MlKemParameterSet.MlKem768, original.PublicKey.Encoded);
        var privateKey = MlKemCrypto.ImportPrivateKey(MlKemParameterSet.MlKem768, original.PrivateKey.Encoded);

        var enc = MlKemCrypto.Encapsulate(publicKey);
        var secret = MlKemCrypto.Decapsulate(privateKey, enc.Ciphertext);
        Assert.Equal(enc.SharedSecret, secret);
    }

    [Fact]
    public void ImportPublicKey_WrongLength_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() =>
            MlKemCrypto.ImportPublicKey(MlKemParameterSet.MlKem768, new byte[16]));
    }

    [Fact]
    public void Decapsulate_WrongCiphertextLength_ShouldThrow()
    {
        var keys = MlKemCrypto.GenerateKeyPair(MlKemParameterSet.MlKem768);
        Assert.Throws<ArgumentException>(() =>
            MlKemCrypto.Decapsulate(keys.PrivateKey, new byte[32]));
    }
}
