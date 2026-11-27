namespace ShortP2P.Crypto;

/// <summary>Result of ML-KEM encapsulation: ciphertext for the peer and local shared secret.</summary>
public sealed class MlKemEncapsulationResult
{
    internal MlKemEncapsulationResult(byte[] ciphertext, byte[] sharedSecret)
    {
        Ciphertext = ciphertext ?? throw new ArgumentNullException(nameof(ciphertext));
        SharedSecret = sharedSecret ?? throw new ArgumentNullException(nameof(sharedSecret));
    }

    /// <summary>Encapsulation ciphertext to send to the peer that holds the private key.</summary>
    public byte[] Ciphertext { get; }

    /// <summary>Shared secret (32 bytes for all FIPS 203 ML-KEM parameter sets).</summary>
    public byte[] SharedSecret { get; }
}
