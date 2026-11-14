namespace ShortP2P.MessengerServer.Contracts.Dtos;

/// <summary>Authenticated client updates whether they appear in search / GetClients directory UI.</summary>
public sealed class ChangeVisibilityRequest
{
    /// <summary>When false, hide from search; invites and other traffic are unaffected.</summary>
    public required bool VisibleForSearch { get; init; }
}
