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
        Assert.Equal(parameterSet, alice.PublicKey.ParameterSet);
        Assert.Equal(parameterSet, alice.PrivateKey.ParameterSet);
        Assert.Equal(MlKemCrypto.PublicKeyByteLength(parameterSet), alice.PublicKey.Encoded.Length);
        Assert.Equal(MlKemCrypto.PrivateKeyByteLength(parameterSet), alice.PrivateKey.Encoded.Length);

        var bob = MlKemCrypto.Encapsulate(alice.PublicKey);
        Assert.Equal(MlKemCrypto.CiphertextByteLength(parameterSet), bob.Ciphertext.Length);
        Assert.Equal(MlKemCrypto.SharedSecretByteLength, bob.SharedSecret.Length);

        var aliceSecret = MlKemCrypto.Decapsulate(alice.PrivateKey, bob.Ciphertext);
        Assert.Equal(bob.SharedSecret, aliceSecret);
    }

    [Fact]
    public void GenerateKeyPair_Default_ShouldUseMlKem768()
    {
        var keys = MlKemCrypto.GenerateKeyPair();
        Assert.Equal(MlKemParameterSet.MlKem768, keys.PublicKey.ParameterSet);
        Assert.Equal(MlKemParameterSet.MlKem768, keys.PrivateKey.ParameterSet);
        Assert.Equal(1184, keys.PublicKey.Encoded.Length);
        Assert.Equal(2400, keys.PrivateKey.Encoded.Length);

        var enc = MlKemCrypto.Encapsulate(keys.PublicKey);
        Assert.Equal(1088, enc.Ciphertext.Length);
        Assert.Equal(32, enc.SharedSecret.Length);
        Assert.Equal(enc.SharedSecret, MlKemCrypto.Decapsulate(keys.PrivateKey, enc.Ciphertext));
    }

    [Fact]
    public void DefaultParameterSet_ShouldBeMlKem768()
    {
        Assert.Equal(MlKemParameterSet.MlKem768, MlKemCrypto.DefaultParameterSet);
        Assert.Equal("ML-KEM", MlKemCrypto.CapabilityName);
        Assert.True(MlKemCrypto.IsSupported);
    }

    [Fact]
    public void MlKem768_ByteLengths_ShouldMatchFips203()
    {
        Assert.Equal(1184, MlKemCrypto.PublicKeyByteLength(MlKemParameterSet.MlKem768));
        Assert.Equal(2400, MlKemCrypto.PrivateKeyByteLength(MlKemParameterSet.MlKem768));
        Assert.Equal(1088, MlKemCrypto.CiphertextByteLength(MlKemParameterSet.MlKem768));
        Assert.Equal(32, MlKemCrypto.SharedSecretByteLength);
    }

    [Fact]
    public void ImportPublicAndPrivate_ShouldRoundTrip()
    {
        var original = MlKemCrypto.GenerateKeyPair(MlKemParameterSet.MlKem768);
        var publicKey = MlKemCrypto.ImportPublicKey(MlKemParameterSet.MlKem768, original.PublicKey.Encoded);
        var privateKey = MlKemCrypto.ImportPrivateKey(MlKemParameterSet.MlKem768, original.PrivateKey.Encoded);

        Assert.Equal(original.PublicKey.Encoded, publicKey.Encoded);
        Assert.Equal(original.PrivateKey.Encoded, privateKey.Encoded);

        var enc = MlKemCrypto.Encapsulate(publicKey);
        var secret = MlKemCrypto.Decapsulate(privateKey, enc.Ciphertext);
        Assert.Equal(enc.SharedSecret, secret);
    }

    [Fact]
    public void ImportPublicKey_ShouldCopyBytes()
    {
        var original = MlKemCrypto.GenerateKeyPair(MlKemParameterSet.MlKem768);
        var buffer = (byte[])original.PublicKey.Encoded.Clone();
        var imported = MlKemCrypto.ImportPublicKey(MlKemParameterSet.MlKem768, buffer);
        buffer[0] ^= 0xFF;
        Assert.Equal(original.PublicKey.Encoded, imported.Encoded);
    }

    [Fact]
    public void ImportPrivateKey_ShouldCopyBytes()
    {
        var original = MlKemCrypto.GenerateKeyPair(MlKemParameterSet.MlKem768);
        var buffer = (byte[])original.PrivateKey.Encoded.Clone();
        var imported = MlKemCrypto.ImportPrivateKey(MlKemParameterSet.MlKem768, buffer);
        buffer[0] ^= 0xFF;
        Assert.Equal(original.PrivateKey.Encoded, imported.Encoded);
    }

    [Fact]
    public void ImportPublicKey_WrongLength_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() =>
            MlKemCrypto.ImportPublicKey(MlKemParameterSet.MlKem768, new byte[16]));
        Assert.Throws<ArgumentException>(() =>
            MlKemCrypto.ImportPublicKey(MlKemParameterSet.MlKem768, new byte[800]));
    }

    [Fact]
    public void ImportPrivateKey_WrongLength_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() =>
            MlKemCrypto.ImportPrivateKey(MlKemParameterSet.MlKem768, new byte[32]));
        Assert.Throws<ArgumentException>(() =>
            MlKemCrypto.ImportPrivateKey(MlKemParameterSet.MlKem768, new byte[1632]));
    }

    [Fact]
    public void ImportPrivateKey_InvalidEncoding_ShouldThrow()
    {
        var junk = new byte[MlKemCrypto.PrivateKeyByteLength(MlKemParameterSet.MlKem768)];
        Assert.ThrowsAny<Exception>(() =>
            MlKemCrypto.ImportPrivateKey(MlKemParameterSet.MlKem768, junk));
    }

    [Fact]
    public void ImportPublicKey_Null_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MlKemCrypto.ImportPublicKey(MlKemParameterSet.MlKem768, null!));
    }

    [Fact]
    public void ImportPrivateKey_Null_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MlKemCrypto.ImportPrivateKey(MlKemParameterSet.MlKem768, null!));
    }

    [Fact]
    public void Encapsulate_Null_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() => MlKemCrypto.Encapsulate(null!));
    }

    [Fact]
    public void Decapsulate_NullArguments_ShouldThrow()
    {
        var keys = MlKemCrypto.GenerateKeyPair(MlKemParameterSet.MlKem768);
        Assert.Throws<ArgumentNullException>(() => MlKemCrypto.Decapsulate(null!, new byte[1088]));
        Assert.Throws<ArgumentNullException>(() => MlKemCrypto.Decapsulate(keys.PrivateKey, null!));
    }

    [Fact]
    public void Decapsulate_WrongCiphertextLength_ShouldThrow()
    {
        var keys = MlKemCrypto.GenerateKeyPair(MlKemParameterSet.MlKem768);
        Assert.Throws<ArgumentException>(() =>
            MlKemCrypto.Decapsulate(keys.PrivateKey, new byte[32]));
        Assert.Throws<ArgumentException>(() =>
            MlKemCrypto.Decapsulate(keys.PrivateKey, new byte[768]));
        Assert.Throws<ArgumentException>(() =>
            MlKemCrypto.Decapsulate(
                keys.PrivateKey,
                new byte[MlKemCrypto.CiphertextByteLength(MlKemParameterSet.MlKem768) + 1]));
    }

    [Fact]
    public void Decapsulate_TamperedCiphertext_ShouldNotMatchSharedSecret()
    {
        var keys = MlKemCrypto.GenerateKeyPair(MlKemParameterSet.MlKem768);
        var enc = MlKemCrypto.Encapsulate(keys.PublicKey);
        var tampered = (byte[])enc.Ciphertext.Clone();
        tampered[10] ^= 0x5A;

        // ML-KEM implicit rejection: decapsulation succeeds but yields a different secret.
        var recovered = MlKemCrypto.Decapsulate(keys.PrivateKey, tampered);
        Assert.Equal(MlKemCrypto.SharedSecretByteLength, recovered.Length);
        Assert.False(enc.SharedSecret.AsSpan().SequenceEqual(recovered));
    }

    [Fact]
    public void Decapsulate_WrongPrivateKey_ShouldNotMatchSharedSecret()
    {
        var alice = MlKemCrypto.GenerateKeyPair(MlKemParameterSet.MlKem768);
        var other = MlKemCrypto.GenerateKeyPair(MlKemParameterSet.MlKem768);
        var enc = MlKemCrypto.Encapsulate(alice.PublicKey);

        var recovered = MlKemCrypto.Decapsulate(other.PrivateKey, enc.Ciphertext);
        Assert.False(enc.SharedSecret.AsSpan().SequenceEqual(recovered));
    }

    [Fact]
    public void ByteLengthHelpers_InvalidParameterSet_ShouldThrow()
    {
        const MlKemParameterSet invalid = (MlKemParameterSet)999;
        Assert.Throws<ArgumentOutOfRangeException>(() => MlKemCrypto.PublicKeyByteLength(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => MlKemCrypto.PrivateKeyByteLength(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => MlKemCrypto.CiphertextByteLength(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => MlKemCrypto.GenerateKeyPair(invalid));
    }

    [Fact]
    public void TwoEncapsulations_ShouldProduceDistinctCiphertexts()
    {
        var keys = MlKemCrypto.GenerateKeyPair(MlKemParameterSet.MlKem768);
        var a = MlKemCrypto.Encapsulate(keys.PublicKey);
        var b = MlKemCrypto.Encapsulate(keys.PublicKey);
        Assert.False(a.Ciphertext.AsSpan().SequenceEqual(b.Ciphertext));
        Assert.Equal(a.SharedSecret, MlKemCrypto.Decapsulate(keys.PrivateKey, a.Ciphertext));
        Assert.Equal(b.SharedSecret, MlKemCrypto.Decapsulate(keys.PrivateKey, b.Ciphertext));
    }

    [Fact]
    public void SetFeatureEnabled_False_ShouldBlockCryptoOperations()
    {
        MlKemCrypto.SetFeatureEnabled(true);
        var keys = MlKemCrypto.GenerateKeyPair(MlKemParameterSet.MlKem768);
        try
        {
            MlKemCrypto.SetFeatureEnabled(false);
            Assert.False(MlKemCrypto.IsFeatureEnabled);
            Assert.Throws<InvalidOperationException>(() => MlKemCrypto.GenerateKeyPair());
            Assert.Throws<InvalidOperationException>(() => MlKemCrypto.Encapsulate(keys.PublicKey));
            Assert.Throws<InvalidOperationException>(() =>
                MlKemCrypto.Decapsulate(keys.PrivateKey, new byte[1088]));
        }
        finally
        {
            MlKemCrypto.SetFeatureEnabled(true);
        }
    }
}
