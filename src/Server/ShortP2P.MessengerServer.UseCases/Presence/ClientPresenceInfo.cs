using ShortP2P.MessengerServer.Domain;

namespace ShortP2P.MessengerServer.UseCases.Presence;

public sealed record ClientPresenceInfo(
    string NetworkId,
    string Nick,
    ClientOnlineStatus Status,
    DateTime LastSeenAtUtc,
    bool VisibleForSearch = true);
