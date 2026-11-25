namespace ShortP2P.Crypto;

/// <summary>
///     V2 peer identity: Ed25519 verification key + X25519 static key for handshake ECDH.
/// </summary>
public sealed class IdentityPublicKey
{
    public const int Ed25519PublicKeyLength = 32;
    public const int X25519PublicKeyLength = 32;

    public IdentityPublicKey(byte[] ed25519Public, byte[] x25519Public)
    {
        Require.NotNull(ed25519Public);
        Require.NotNull(x25519Public);
        if (ed25519Public.Length != Ed25519PublicKeyLength)
            throw new ArgumentException($"Ed25519 public key must be {Ed25519PublicKeyLength} bytes.",
                nameof(ed25519Public));
        if (x25519Public.Length != X25519PublicKeyLength)
            throw new ArgumentException($"X25519 public key must be {X25519PublicKeyLength} bytes.",
                nameof(x25519Public));

        Ed25519Public = (byte[])ed25519Public.Clone();
        X25519Public = (byte[])x25519Public.Clone();
    }

    public byte[] Ed25519Public { get; }
    public byte[] X25519Public { get; }
}
