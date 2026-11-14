namespace ShortP2P.MessengerServer.UseCases.Auth;

public sealed record RegisterClientCommand(
    string Nick,
    string NetworkId,
    string Password,
    string DeviceId,
    bool VisibleForSearch = true);
