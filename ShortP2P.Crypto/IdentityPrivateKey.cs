namespace ShortP2P.Crypto;

/// <summary>
///     V2 local identity secrets: Ed25519 signing seed + X25519 static private key.
/// </summary>
public sealed class IdentityPrivateKey
{
    public const int Ed25519SeedLength = 32;
    public const int X25519PrivateKeyLength = 32;

    public IdentityPrivateKey(byte[] ed25519Seed, byte[] ed25519Public, byte[] x25519Private, byte[] x25519Public)
    {
        Require.NotNull(ed25519Seed);
        Require.NotNull(ed25519Public);
        Require.NotNull(x25519Private);
        Require.NotNull(x25519Public);
        if (ed25519Seed.Length != Ed25519SeedLength)
            throw new ArgumentException($"Ed25519 seed must be {Ed25519SeedLength} bytes.", nameof(ed25519Seed));
        if (ed25519Public.Length != IdentityPublicKey.Ed25519PublicKeyLength)
            throw new ArgumentException($"Ed25519 public key must be {IdentityPublicKey.Ed25519PublicKeyLength} bytes.",
                nameof(ed25519Public));
        if (x25519Private.Length != X25519PrivateKeyLength)
            throw new ArgumentException($"X25519 private key must be {X25519PrivateKeyLength} bytes.",
                nameof(x25519Private));
        if (x25519Public.Length != IdentityPublicKey.X25519PublicKeyLength)
            throw new ArgumentException($"X25519 public key must be {IdentityPublicKey.X25519PublicKeyLength} bytes.",
                nameof(x25519Public));

        Ed25519Seed = (byte[])ed25519Seed.Clone();
        Ed25519Public = (byte[])ed25519Public.Clone();
        X25519Private = (byte[])x25519Private.Clone();
        X25519Public = (byte[])x25519Public.Clone();
    }

    public byte[] Ed25519Seed { get; }
    public byte[] Ed25519Public { get; }
    public byte[] X25519Private { get; }
    public byte[] X25519Public { get; }

    public IdentityPublicKey PublicKey => new(Ed25519Public, X25519Public);
}
