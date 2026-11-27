namespace ShortP2P.Crypto;

/// <summary>Generated Ed25519 + X25519 identity key pair.</summary>
public sealed class IdentityKeyPair
{
    internal IdentityKeyPair(IdentityPublicKey publicKey, IdentityPrivateKey privateKey)
    {
        PublicKey = publicKey ?? throw new ArgumentNullException(nameof(publicKey));
        PrivateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));
    }

    public IdentityPublicKey PublicKey { get; }
    public IdentityPrivateKey PrivateKey { get; }
}
