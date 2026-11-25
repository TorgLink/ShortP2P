namespace ShortP2P.Client.Services;

internal sealed class PendingPeerKeyChange
{
    public required string PublicKeyJson { get; init; }
    public string? Nickname { get; init; }
    public string? Host { get; init; }
    public PeerKeySource? KeySource { get; init; }
}
