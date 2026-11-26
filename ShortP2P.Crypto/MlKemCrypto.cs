using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace ShortP2P.Crypto;

/// <summary>
///     Trial managed ML-KEM (FIPS 203) via Bouncy Castle — same code path on all platforms
///     (does not use OS CNG / OpenSSL PQC). Default parameter set is ML-KEM-768.
///     Not yet wired into <see cref="P2PHandshake"/>; intended for SHO-34 experimental path.
/// </summary>
public static class MlKemCrypto
{
    /// <summary>Capability flag name for API / capability lists (absent when unsupported).</summary>
    public const string CapabilityName = "ML-KEM";

    /// <summary>Default parameter set for the experimental quantum session path.</summary>
    public static MlKemParameterSet DefaultParameterSet => MlKemParameterSet.MlKem768;

    /// <summary>Always true: managed Bouncy Castle ML-KEM does not depend on OS PQC support.</summary>
    public static bool IsSupported => true;

    public static MlKemKeyPair GenerateKeyPair(MlKemParameterSet parameterSet = MlKemParameterSet.MlKem768)
    {
        var bcParams = ToBcParameters(parameterSet);
        var random = new SecureRandom();
        var generator = new MLKemKeyPairGenerator();
        generator.Init(new MLKemKeyGenerationParameters(random, bcParams));
        var pair = generator.GenerateKeyPair();

        var publicKey = (MLKemPublicKeyParameters)pair.Public;
        var privateKey = (MLKemPrivateKeyParameters)pair.Private;

        return new MlKemKeyPair(
            new MlKemPublicKey(parameterSet, publicKey.GetEncoded()),
            new MlKemPrivateKey(parameterSet, privateKey.GetEncoded()));
    }

    public static MlKemPublicKey ImportPublicKey(MlKemParameterSet parameterSet, byte[] encoded)
    {
        Require.NotNull(encoded);
        var bcParams = ToBcParameters(parameterSet);
        var expected = PublicKeyByteLength(parameterSet);
        if (encoded.Length != expected)
        {
            throw new ArgumentException(
                $"ML-KEM public key must be {expected} bytes for {parameterSet}, got {encoded.Length}.",
                nameof(encoded));
        }

        // Validate encoding through BC before wrapping.
        _ = MLKemPublicKeyParameters.FromEncoding(bcParams, encoded);
        var copy = new byte[encoded.Length];
        Buffer.BlockCopy(encoded, 0, copy, 0, encoded.Length);
        return new MlKemPublicKey(parameterSet, copy);
    }

    public static MlKemPrivateKey ImportPrivateKey(MlKemParameterSet parameterSet, byte[] encoded)
    {
        Require.NotNull(encoded);
        var bcParams = ToBcParameters(parameterSet);
        var expected = PrivateKeyByteLength(parameterSet);
        if (encoded.Length != expected)
        {
            throw new ArgumentException(
                $"ML-KEM private key must be {expected} bytes for {parameterSet}, got {encoded.Length}.",
                nameof(encoded));
        }

        _ = MLKemPrivateKeyParameters.FromEncoding(bcParams, encoded);
        var copy = new byte[encoded.Length];
        Buffer.BlockCopy(encoded, 0, copy, 0, encoded.Length);
        return new MlKemPrivateKey(parameterSet, copy);
    }

    /// <summary>
    ///     Encapsulate to <paramref name="remotePublicKey"/>: returns ciphertext for the peer
    ///     and the local shared secret.
    /// </summary>
    public static MlKemEncapsulationResult Encapsulate(MlKemPublicKey remotePublicKey)
    {
        Require.NotNull(remotePublicKey);
        var bcParams = ToBcParameters(remotePublicKey.ParameterSet);
        var publicKey = MLKemPublicKeyParameters.FromEncoding(bcParams, remotePublicKey.Encoded);

        var encapsulator = new MLKemEncapsulator(bcParams);
        encapsulator.Init(publicKey);

        var ciphertext = new byte[encapsulator.EncapsulationLength];
        var sharedSecret = new byte[encapsulator.SecretLength];
        encapsulator.Encapsulate(
            ciphertext, 0, ciphertext.Length,
            sharedSecret, 0, sharedSecret.Length);

        return new MlKemEncapsulationResult(ciphertext, sharedSecret);
    }

    /// <summary>Decapsulate <paramref name="ciphertext"/> with the local private key.</summary>
    public static byte[] Decapsulate(MlKemPrivateKey localPrivateKey, byte[] ciphertext)
    {
        Require.NotNull(localPrivateKey);
        Require.NotNull(ciphertext);

        var bcParams = ToBcParameters(localPrivateKey.ParameterSet);
        var privateKey = MLKemPrivateKeyParameters.FromEncoding(bcParams, localPrivateKey.Encoded);

        var decapsulator = new MLKemDecapsulator(bcParams);
        decapsulator.Init(privateKey);

        if (ciphertext.Length != decapsulator.EncapsulationLength)
        {
            throw new ArgumentException(
                $"ML-KEM ciphertext must be {decapsulator.EncapsulationLength} bytes for {localPrivateKey.ParameterSet}, got {ciphertext.Length}.",
                nameof(ciphertext));
        }

        var sharedSecret = new byte[decapsulator.SecretLength];
        decapsulator.Decapsulate(
            ciphertext, 0, ciphertext.Length,
            sharedSecret, 0, sharedSecret.Length);
        return sharedSecret;
    }

    public static int PublicKeyByteLength(MlKemParameterSet parameterSet) => parameterSet switch
    {
        MlKemParameterSet.MlKem512 => 800,
        MlKemParameterSet.MlKem768 => 1184,
        MlKemParameterSet.MlKem1024 => 1568,
        _ => throw new ArgumentOutOfRangeException(nameof(parameterSet), parameterSet, null),
    };

    public static int PrivateKeyByteLength(MlKemParameterSet parameterSet) => parameterSet switch
    {
        MlKemParameterSet.MlKem512 => 1632,
        MlKemParameterSet.MlKem768 => 2400,
        MlKemParameterSet.MlKem1024 => 3168,
        _ => throw new ArgumentOutOfRangeException(nameof(parameterSet), parameterSet, null),
    };

    public static int CiphertextByteLength(MlKemParameterSet parameterSet) => parameterSet switch
    {
        MlKemParameterSet.MlKem512 => 768,
        MlKemParameterSet.MlKem768 => 1088,
        MlKemParameterSet.MlKem1024 => 1568,
        _ => throw new ArgumentOutOfRangeException(nameof(parameterSet), parameterSet, null),
    };

    /// <summary>Shared secret length is 32 bytes for all FIPS 203 ML-KEM sets.</summary>
    public const int SharedSecretByteLength = 32;

    private static MLKemParameters ToBcParameters(MlKemParameterSet parameterSet) => parameterSet switch
    {
        MlKemParameterSet.MlKem512 => MLKemParameters.ml_kem_512,
        MlKemParameterSet.MlKem768 => MLKemParameters.ml_kem_768,
        MlKemParameterSet.MlKem1024 => MLKemParameters.ml_kem_1024,
        _ => throw new ArgumentOutOfRangeException(nameof(parameterSet), parameterSet, null),
    };
}
