namespace ShortP2P.Client.Services;

/// <summary>
///     Invite (or route update) offered a different public key for an already-pinned peer.
///     UI should ask the user; call <see cref="ChatRepository.AcceptPendingPeerKeyChangeAsync"/> or reject.
/// </summary>
public sealed class PeerKeyChangeProposedEventArgs : EventArgs
{
    public required int ChatId { get; init; }
    public required string PeerNickname { get; init; }
    public required string PeerNetworkIdShort { get; init; }
    public required string PreviousSafetyNumber { get; init; }
    public required string ProposedSafetyNumber { get; init; }
}
