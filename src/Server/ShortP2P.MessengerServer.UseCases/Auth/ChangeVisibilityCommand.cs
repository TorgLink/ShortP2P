namespace ShortP2P.MessengerServer.UseCases.Auth;

public sealed record ChangeVisibilityCommand(string NetworkId, bool VisibleForSearch);
