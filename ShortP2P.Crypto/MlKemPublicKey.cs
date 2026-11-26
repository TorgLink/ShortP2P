namespace ShortP2P.Crypto;

/// <summary>ML-KEM encapsulation (public) key bytes for a fixed parameter set.</summary>
public sealed class MlKemPublicKey
{
    internal MlKemPublicKey(MlKemParameterSet parameterSet, byte[] encoded)
    {
        ParameterSet = parameterSet;
        Encoded = encoded ?? throw new ArgumentNullException(nameof(encoded));
    }

    public MlKemParameterSet ParameterSet { get; }

    /// <summary>FIPS 203 public key encoding.</summary>
    public byte[] Encoded { get; }
}
