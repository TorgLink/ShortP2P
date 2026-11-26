namespace ShortP2P.Crypto;

/// <summary>ML-KEM decapsulation (private) key bytes for a fixed parameter set.</summary>
public sealed class MlKemPrivateKey
{
    internal MlKemPrivateKey(MlKemParameterSet parameterSet, byte[] encoded)
    {
        ParameterSet = parameterSet;
        Encoded = encoded ?? throw new ArgumentNullException(nameof(encoded));
    }

    public MlKemParameterSet ParameterSet { get; }

    /// <summary>FIPS 203 private key encoding.</summary>
    public byte[] Encoded { get; }
}
