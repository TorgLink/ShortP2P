namespace ShortP2P.MessengerServer.Contracts;

/// <summary>
/// HTTPS API route constants for messenger server.
/// Current API version is <c>1</c>; it is part of <see cref="Prefix"/> (<c>/api/v1</c>).
/// Controllers mount resources under that prefix, e.g. <c>[Route($"{ApiRoutes.Prefix}/bots")]</c> → <c>/api/v1/bots</c>.
/// </summary>
public static class ApiRoutes
{
    /// <summary>Versioned base path for the current HTTP API (<c>/api/v1</c>).</summary>
    public const string Prefix = "/api/v1";

    public const string Register = Prefix + "/auth/register";
    public const string Login = Prefix + "/auth/login";

    /// <summary>POST update caller's search visibility (<see cref="Dtos.ChangeVisibilityRequest"/>).</summary>
    public const string ChangeVisibility = Prefix + "/auth/visibility";

    public const string ServerCertificate = Prefix + "/server/certificate";

    public const string Chats = Prefix + "/chats";
    public const string ChatRequests = Prefix + "/chats/requests";

    public const string Messages = Prefix + "/messages";
    public const string MessageReceipts = Prefix + "/messages/receipts";

    public const string Blobs = Prefix + "/blobs";

    /// <summary>GET long-poll inbox: messages + chat requests for the caller's device.</summary>
    public const string EventsPoll = Prefix + "/events/poll";

    /// <summary>POST ephemeral peer-profile forward (0x44/0x45) to an online peer.</summary>
    public const string Forward = Prefix + "/forward";

    public const string Clients = Prefix + "/clients";

    /// <summary>GET anonymous TotalPower (host hardware score).</summary>
    public const string ServerTechPower = Prefix + "/server-tech/power";

    /// <summary>GET anonymous FreePowers (host free capacity %).</summary>
    public const string ServerTechFreePowers = Prefix + "/server-tech/free-powers";

    /// <summary>GET anonymous liveness ping (200 OK).</summary>
    public const string ServerTechPing = Prefix + "/server-tech/ping";

    /// <summary>GET anonymous health ping (200 OK).</summary>
    public const string HealthPing = Prefix + "/health/ping";

    /// <summary>GET gossip + ratings: ensure the named server exists (default 0.8) and return all ratings.</summary>
    public const string TrustAskRating = Prefix + "/trust/ask-rating";

    /// <summary>GET trusted peers (rating ≥ 0.3) with ratings.</summary>
    public const string TrustAskServers = Prefix + "/trust/ask-servers";

    /// <summary>POST claim about another server (UNAVAILABLE / MALFUNCTIONED / WRONGCERT).</summary>
    public const string TrustClaim = Prefix + "/trust/claim";

    /// <summary>POST long-poll encrypted messages from bots to this client (JWT).</summary>
    public const string BotClientWaitForMessages = Prefix + "/bot_client_interaction/wait_for_messages";

    /// <summary>POST one encrypted client → bot message (JWT).</summary>
    public const string BotClientSendMessage = Prefix + "/bot_client_interaction/send_message";

    /// <summary>POST client receipt for a bot → client message (JWT); server drops its copy.</summary>
    public const string BotClientReceipts = Prefix + "/bot_client_interaction/receipts";

    /// <summary>GET registered bots for authorized clients (public fields only).</summary>
    public const string Bots = Prefix + "/bots";

    /// <summary>GET <c>/bots/{botNetworkId}/version</c> — one bot's network id and version (JWT).</summary>
    public static string BotVersion(string botNetworkId) =>
        $"{Bots}/{Uri.EscapeDataString(botNetworkId.Trim())}/version";

    /// <summary>GET <c>/bots/{botNetworkId}/commands</c> — one bot's network id and command list (JWT).</summary>
    public static string BotCommands(string botNetworkId) =>
        $"{Bots}/{Uri.EscapeDataString(botNetworkId.Trim())}/commands";

    /// <summary>POST claim about a registered bot (JWT). Rating is settled by the hourly antifraud service.</summary>
    public const string BotClaim = Prefix + "/bots/claim";

    /// <summary>
    /// POST register a bot (anonymous; TLS fingerprint required); returns <c>botKey</c> once + server fingerprint.
    /// </summary>
    public const string BotServerRegister = Prefix + "/bot_server_interaction/register";

    /// <summary>POST authorize a bot with <c>botKey</c> + certificate fingerprint (anonymous).</summary>
    public const string BotServerLogin = Prefix + "/bot_server_interaction/login";

    /// <summary>POST remove a bot by <c>networkId</c> + <c>botKey</c> (anonymous).</summary>
    public const string BotServerRemove = Prefix + "/bot_server_interaction/remove";

    /// <summary>POST bot → clients encrypted messages (store-and-forward until client receipt).</summary>
    public const string BotServerSendMessages = Prefix + "/bot_server_interaction/send_messages";

    /// <summary>POST long-poll client → bot messages (auth via <c>botKey</c> in body).</summary>
    public const string BotServerWaitForIncomeMessages =
        Prefix + "/bot_server_interaction/wait_for_income_messages";

    /// <summary>POST bot receipt for a client → bot message (auth via <c>botKey</c>); drops the message and its chunks.</summary>
    public const string BotServerReceipts = Prefix + "/bot_server_interaction/receipts";
}
