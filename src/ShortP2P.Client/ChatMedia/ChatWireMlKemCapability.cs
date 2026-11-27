namespace ShortP2P.Client.ChatMedia;

/// <summary>
/// Encrypted-path ML-KEM capability advertisement (works over messenger-server relay).
/// <see cref="WantsMlKem"/> mirrors the local EnableMlKem setting.
/// </summary>
public sealed record ChatWireMlKemCapability(bool WantsMlKem) : ChatWireMessage;
