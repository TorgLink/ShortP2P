using System.Security.Cryptography;
using ShortP2P.Crypto;

namespace ShortP2P.Client.Services.MessengerServers;

/// <summary>
/// Messenger-server envelopes, opaque to the server.
/// Version 1 wraps a fresh AES-GCM key in RSA for every payload (legacy, still used for bots).
/// Version 2 wraps one session key in RSA (establish). Later payloads and the probe acknowledgement
/// are AES-GCM only. A new RSA wrap happens only when the probe fails.
/// </summary>
public static class MessengerServerPayloadCodec
{
    private const byte VersionLegacy = 1;
    private const byte VersionSession = 2;
    private const byte KindEstablish = 1;
    private const byte KindData = 2;
    private const byte KindProbeAck = 4;
    private const byte KindRekey = 5;

    internal const int AesKeyBytes = 32;
    internal const int SessionIdBytes = 16;
    internal const int ProbeTokenBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static string EncryptToBase64(ReadOnlySpan<byte> plaintext, RsaPublicKey recipientPublicKey) =>
        Convert.ToBase64String(Encrypt(plaintext, recipientPublicKey));

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, RsaPublicKey recipientPublicKey)
    {
        Require.NotNull(recipientPublicKey);

        var aesKey = GetRandomBytes(AesKeyBytes);
        var nonce = GetRandomBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(aesKey, TagBytes))
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var keyMaterial = new byte[AesKeyBytes + NonceBytes];
        Buffer.BlockCopy(aesKey, 0, keyMaterial, 0, AesKeyBytes);
        Buffer.BlockCopy(nonce, 0, keyMaterial, AesKeyBytes, NonceBytes);

        byte[] wrappedKey;
        using (var rsa = CreateRsa())
        {
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = recipientPublicKey.Modulus,
                Exponent = recipientPublicKey.Exponent
            });
            wrappedKey = rsa.Encrypt(keyMaterial, RSAEncryptionPadding.OaepSHA256);
        }

        CryptographicOperations.ZeroMemory(aesKey);
        CryptographicOperations.ZeroMemory(keyMaterial);

        var result = new byte[1 + 2 + wrappedKey.Length + ciphertext.Length + TagBytes];
        result[0] = VersionLegacy;
        result[1] = (byte)(wrappedKey.Length >> 8);
        result[2] = (byte)wrappedKey.Length;
        Buffer.BlockCopy(wrappedKey, 0, result, 3, wrappedKey.Length);
        var bodyOffset = 3 + wrappedKey.Length;
        Buffer.BlockCopy(ciphertext, 0, result, bodyOffset, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, bodyOffset + ciphertext.Length, TagBytes);
        return result;
    }

    public static byte[] DecryptFromBase64(string encryptedDataBase64, RsaPrivateKey privateKey)
    {
        Require.NotNullOrWhiteSpace(encryptedDataBase64);
        return Decrypt(Convert.FromBase64String(encryptedDataBase64.Trim()), privateKey);
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> blob, RsaPrivateKey privateKey)
    {
        Require.NotNull(privateKey);

        if (blob.Length < 1 + 2 + 1 + TagBytes)
            throw new CryptographicException("Server payload is too short.");
        if (blob[0] != VersionLegacy)
            throw new CryptographicException($"Unsupported legacy server payload version: {blob[0]}.");

        var keyLen = (blob[1] << 8) | blob[2];
        if (keyLen <= 0 || 3 + keyLen + TagBytes > blob.Length)
            throw new CryptographicException("Invalid server payload key length.");

        var wrappedKey = blob.Slice(3, keyLen).ToArray();
        var cipherAndTag = blob.Slice(3 + keyLen);
        var cipherLen = cipherAndTag.Length - TagBytes;
        if (cipherLen < 0)
            throw new CryptographicException("Invalid server payload ciphertext.");

        var ciphertext = cipherAndTag[..cipherLen].ToArray();
        var tag = cipherAndTag[cipherLen..].ToArray();

        byte[] keyMaterial;
        using (var rsa = CreateRsa())
        {
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = privateKey.Modulus,
                Exponent = privateKey.Exponent,
                D = privateKey.D,
                P = privateKey.P,
                Q = privateKey.Q,
                DP = privateKey.DP,
                DQ = privateKey.DQ,
                InverseQ = privateKey.InverseQ
            });
            keyMaterial = rsa.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256);
        }

        if (keyMaterial.Length != AesKeyBytes + NonceBytes)
            throw new CryptographicException("Invalid unwrapped key material.");

        var aesKey = keyMaterial.AsSpan(0, AesKeyBytes);
        var nonce = keyMaterial.AsSpan(AesKeyBytes, NonceBytes);
        var plaintext = new byte[cipherLen];
        using (var aes = new AesGcm(aesKey.ToArray(), TagBytes))
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

        CryptographicOperations.ZeroMemory(keyMaterial);
        return plaintext;
    }

    /// <summary>
    /// Encrypts a chat wire for a human peer. The first send (and any send after a failed probe)
    /// RSA-wraps a new session key and carries the wire under that key. Later sends are symmetric.
    /// </summary>
    public static byte[] EncryptForSession(
        ReadOnlySpan<byte> plaintext,
        RsaPublicKey recipientPublicKey,
        string peerId,
        ServerCipherSessionStore store,
        DateTimeOffset? utcNow = null)
    {
        Require.NotNull(recipientPublicKey);
        Require.NotNull(store);
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var session = store.PrepareOutbound(peerId, now, out var establishedNow);
        return establishedNow
            ? BuildEstablish(session, plaintext, recipientPublicKey)
            : BuildSymmetric(KindData, session, plaintext);
    }

    /// <summary>
    /// Blob ciphertext. Uses the current session when one is alive; otherwise the legacy per-message wrap.
    /// Does not create or rotate a session (the chat message does that).
    /// </summary>
    public static byte[] EncryptPreferSession(
        ReadOnlySpan<byte> plaintext,
        RsaPublicKey recipientPublicKey,
        string peerId,
        ServerCipherSessionStore store,
        DateTimeOffset? utcNow = null)
    {
        Require.NotNull(recipientPublicKey);
        Require.NotNull(store);
        var now = utcNow ?? DateTimeOffset.UtcNow;
        if (store.TryGetUsableOutbound(peerId, now, out var session) && session != null)
            return BuildSymmetric(KindData, session, plaintext);
        return Encrypt(plaintext, recipientPublicKey);
    }

    /// <summary>
    /// Opens version 1 and version 2 envelopes.
    /// Chat wires are returned for persistence. Probe acknowledgements and re-key notices are control frames;
    /// <see cref="ServerPayloadOpenResult.ResponseEnvelope"/> is sent back and is not a chat message.
    /// </summary>
    public static ServerPayloadOpenResult Open(
        ReadOnlySpan<byte> blob,
        RsaPrivateKey privateKey,
        string peerId,
        ServerCipherSessionStore store)
    {
        Require.NotNull(privateKey);
        Require.NotNull(store);
        if (blob.Length < 1)
            throw new CryptographicException("Server payload is too short.");

        if (blob[0] == VersionLegacy)
            return ServerPayloadOpenResult.Chat(Decrypt(blob, privateKey));

        if (blob[0] != VersionSession)
            throw new CryptographicException($"Unsupported server payload version: {blob[0]}.");
        if (blob.Length < 2)
            throw new CryptographicException("Server payload is too short.");

        return blob[1] switch
        {
            KindEstablish => OpenEstablish(blob, privateKey, store),
            KindData => OpenSymmetric(blob, store),
            KindProbeAck => OpenProbeAck(blob, store),
            KindRekey => OpenRekey(blob, peerId, store),
            _ => throw new CryptographicException($"Unsupported server payload kind: {blob[1]}.")
        };
    }

    private static byte[] BuildEstablish(ServerCipherSession session, ReadOnlySpan<byte> chatWire, RsaPublicKey recipient)
    {
        var token = session.OutstandingProbeToken ??
                    throw new CryptographicException("Session is missing its probe token.");
        var plain = new byte[token.Length + chatWire.Length];
        Buffer.BlockCopy(token, 0, plain, 0, token.Length);
        chatWire.CopyTo(plain.AsSpan(token.Length));

        var nonce = GetRandomBytes(NonceBytes);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(session.Key, TagBytes))
            aes.Encrypt(nonce, plain, cipher, tag);

        var wrappedMaterial = new byte[SessionIdBytes + AesKeyBytes];
        Buffer.BlockCopy(session.SessionId, 0, wrappedMaterial, 0, SessionIdBytes);
        Buffer.BlockCopy(session.Key, 0, wrappedMaterial, SessionIdBytes, AesKeyBytes);

        byte[] wrapped;
        using (var rsa = CreateRsa())
        {
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = recipient.Modulus,
                Exponent = recipient.Exponent
            });
            wrapped = rsa.Encrypt(wrappedMaterial, RSAEncryptionPadding.OaepSHA256);
        }

        CryptographicOperations.ZeroMemory(wrappedMaterial);

        var result = new byte[4 + wrapped.Length + NonceBytes + cipher.Length + TagBytes];
        result[0] = VersionSession;
        result[1] = KindEstablish;
        result[2] = (byte)(wrapped.Length >> 8);
        result[3] = (byte)wrapped.Length;
        Buffer.BlockCopy(wrapped, 0, result, 4, wrapped.Length);
        var offset = 4 + wrapped.Length;
        Buffer.BlockCopy(nonce, 0, result, offset, NonceBytes);
        Buffer.BlockCopy(cipher, 0, result, offset + NonceBytes, cipher.Length);
        Buffer.BlockCopy(tag, 0, result, offset + NonceBytes + cipher.Length, TagBytes);
        return result;
    }

    private static byte[] BuildSymmetric(byte kind, ServerCipherSession session, ReadOnlySpan<byte> plaintext)
    {
        var nonce = GetRandomBytes(NonceBytes);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(session.Key, TagBytes))
            aes.Encrypt(nonce, plaintext, cipher, tag);

        var result = new byte[2 + SessionIdBytes + NonceBytes + cipher.Length + TagBytes];
        result[0] = VersionSession;
        result[1] = kind;
        Buffer.BlockCopy(session.SessionId, 0, result, 2, SessionIdBytes);
        Buffer.BlockCopy(nonce, 0, result, 2 + SessionIdBytes, NonceBytes);
        Buffer.BlockCopy(cipher, 0, result, 2 + SessionIdBytes + NonceBytes, cipher.Length);
        Buffer.BlockCopy(tag, 0, result, result.Length - TagBytes, TagBytes);
        return result;
    }

    private static byte[] BuildRekey(ReadOnlySpan<byte> sessionId)
    {
        var result = new byte[2 + SessionIdBytes];
        result[0] = VersionSession;
        result[1] = KindRekey;
        sessionId.CopyTo(result.AsSpan(2));
        return result;
    }

    private static ServerPayloadOpenResult OpenEstablish(
        ReadOnlySpan<byte> blob,
        RsaPrivateKey privateKey,
        ServerCipherSessionStore store)
    {
        if (blob.Length < 4 + NonceBytes + ProbeTokenBytes + TagBytes)
            throw new CryptographicException("Session establish payload is too short.");

        var keyLen = (blob[2] << 8) | blob[3];
        if (keyLen <= 0 || 4 + keyLen + NonceBytes + TagBytes > blob.Length)
            throw new CryptographicException("Invalid session establish key length.");

        var wrapped = blob.Slice(4, keyLen).ToArray();
        byte[] material;
        using (var rsa = CreateRsa())
        {
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = privateKey.Modulus,
                Exponent = privateKey.Exponent,
                D = privateKey.D,
                P = privateKey.P,
                Q = privateKey.Q,
                DP = privateKey.DP,
                DQ = privateKey.DQ,
                InverseQ = privateKey.InverseQ
            });
            material = rsa.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
        }

        if (material.Length != SessionIdBytes + AesKeyBytes)
            throw new CryptographicException("Invalid unwrapped session key.");

        var sessionId = material.AsSpan(0, SessionIdBytes);
        var aesKey = material.AsSpan(SessionIdBytes, AesKeyBytes);
        var body = blob[(4 + keyLen)..];
        var plain = DecryptGcm(aesKey, body);
        if (plain.Length < ProbeTokenBytes)
        {
            CryptographicOperations.ZeroMemory(material);
            throw new CryptographicException("Session establish plaintext is too short.");
        }

        var token = plain.AsSpan(0, ProbeTokenBytes).ToArray();
        var chat = plain.AsSpan(ProbeTokenBytes).ToArray();
        store.RememberInbound(sessionId, aesKey);
        if (!store.TryGetBySessionId(sessionId, out var saved) || saved == null)
        {
            CryptographicOperations.ZeroMemory(material);
            throw new CryptographicException("Session key was not stored.");
        }

        var ack = BuildSymmetric(KindProbeAck, saved, token);
        CryptographicOperations.ZeroMemory(material);
        return ServerPayloadOpenResult.Chat(chat, ack);
    }

    private static ServerPayloadOpenResult OpenSymmetric(
        ReadOnlySpan<byte> blob,
        ServerCipherSessionStore store)
    {
        if (blob.Length < 2 + SessionIdBytes + NonceBytes + TagBytes)
            throw new CryptographicException("Symmetric server payload is too short.");
        if (blob[1] != KindData)
            throw new CryptographicException("Symmetric server payload kind mismatch.");

        var sessionId = blob.Slice(2, SessionIdBytes);
        if (!store.TryGetBySessionId(sessionId, out var session) || session == null)
            return ServerPayloadOpenResult.Control(BuildRekey(sessionId));

        var body = blob[(2 + SessionIdBytes)..];
        var plain = DecryptGcm(session.Key, body);
        return ServerPayloadOpenResult.Chat(plain);
    }

    private static ServerPayloadOpenResult OpenProbeAck(ReadOnlySpan<byte> blob, ServerCipherSessionStore store)
    {
        if (blob.Length < 2 + SessionIdBytes + NonceBytes + TagBytes)
            throw new CryptographicException("Probe acknowledgement is too short.");

        var sessionId = blob.Slice(2, SessionIdBytes);
        if (!store.TryGetBySessionId(sessionId, out var session) || session == null)
            return ServerPayloadOpenResult.Control();

        var token = DecryptGcm(session.Key, blob[(2 + SessionIdBytes)..]);
        store.TryConfirmProbe(sessionId, token);
        return ServerPayloadOpenResult.Control();
    }

    private static ServerPayloadOpenResult OpenRekey(
        ReadOnlySpan<byte> blob,
        string peerId,
        ServerCipherSessionStore store)
    {
        if (blob.Length != 2 + SessionIdBytes)
            throw new CryptographicException("Re-key notice has an invalid length.");
        store.DropOutboundIfMatch(peerId, blob.Slice(2, SessionIdBytes));
        return ServerPayloadOpenResult.Control();
    }

    private static byte[] DecryptGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonceCipherAndTag)
    {
        if (nonceCipherAndTag.Length < NonceBytes + TagBytes)
            throw new CryptographicException("Symmetric server payload is too short.");
        var nonce = nonceCipherAndTag[..NonceBytes];
        var cipherLen = nonceCipherAndTag.Length - NonceBytes - TagBytes;
        var cipher = nonceCipherAndTag.Slice(NonceBytes, cipherLen);
        var tag = nonceCipherAndTag[^TagBytes..];
        var plain = new byte[cipherLen];
        using var aes = new AesGcm(key.ToArray(), TagBytes);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    private static byte[] GetRandomBytes(int count)
    {
#if NET6_0_OR_GREATER
        return RandomNumberGenerator.GetBytes(count);
#else
        var bytes = new byte[count];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return bytes;
#endif
    }

    /// <summary>
    /// net472 <see cref="RSA.Create()"/> returns <c>RSACryptoServiceProvider</c>, which rejects
    /// <see cref="RSAEncryptionPadding.OaepSHA256"/>. Use CNG so server envelopes match net10.
    /// </summary>
    private static RSA CreateRsa()
    {
#if NETFRAMEWORK
        return new RSACng();
#else
        return RSA.Create();
#endif
    }
}
