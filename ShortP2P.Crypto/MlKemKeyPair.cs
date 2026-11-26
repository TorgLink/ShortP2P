namespace ShortP2P.Crypto;

/// <summary>Generated ML-KEM key pair (managed Bouncy Castle implementation).</summary>
public sealed class MlKemKeyPair
{
    internal MlKemKeyPair(MlKemPublicKey publicKey, MlKemPrivateKey privateKey)
    {
        PublicKey = publicKey ?? throw new ArgumentNullException(nameof(publicKey));
        PrivateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));
    }

    public MlKemPublicKey PublicKey { get; }
    public MlKemPrivateKey PrivateKey { get; }
}
