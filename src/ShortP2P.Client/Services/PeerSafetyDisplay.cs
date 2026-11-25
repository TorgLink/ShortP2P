using ShortP2P.Client.Data;
using ShortP2P.Crypto;

namespace ShortP2P.Client.Services;

public static class PeerSafetyDisplay
{
    public const string MeshWarning =
        "больше нет доверенных серверов, переключаюсь в режим mesh.";

    public const string KeyChangeTitle = "Смена публичного ключа";

    public static string FormatFingerprints(
        string myNick,
        IdentityPublicKey myKey,
        string peerNick,
        string? peerPublicKeyJson)
    {
        var mine = SafetyNumber.FromPublicKey(myKey);
        var peer = SafetyNumber.FromPublicKeyJsonOrEmpty(peerPublicKeyJson);
        if (peer.Length == 0)
            peer = "—";
        return SafetyNumber.FormatPair(myNick, mine, peerNick, peer);
    }

    public static string FormatChannel(string? kind, string? detail)
    {
        var k = (kind ?? "").Trim();
        var d = (detail ?? "").Trim();
        if (k.Length == 0)
            return "";
        if (string.Equals(k, PeerKeySourceKinds.Udp, StringComparison.OrdinalIgnoreCase))
            return "UDP";
        if (string.Equals(k, PeerKeySourceKinds.Bluetooth, StringComparison.OrdinalIgnoreCase))
            return "Bluetooth";
        if (string.Equals(k, PeerKeySourceKinds.Server, StringComparison.OrdinalIgnoreCase))
            return d.Length == 0 ? "Server" : $"Server {FormatServerEndpoint(d)}";
        if (string.Equals(k, PeerKeySourceKinds.Qr, StringComparison.OrdinalIgnoreCase))
            return "QR";
        if (string.Equals(k, PeerKeySourceKinds.Manual, StringComparison.OrdinalIgnoreCase))
            return "вручную";
        return k;
    }

    public static string FormatChannel(ChatEntity chat) =>
        FormatChannel(chat.PeerKeySourceKind, chat.PeerKeySourceDetail);

    /// <summary>
    ///     Prefer the live delivery channel from <paramref name="session"/> (server pin / path)
    ///     over <see cref="ChatEntity.PeerKeySourceKind"/> (key-discovery source).
    /// </summary>
    public static string FormatChannel(ChatEntity chat, ChatP2PSession? session)
    {
        if (session != null && session.IsCommunicatingViaMessengerServer)
        {
            var endpoint = FormatServerEndpoint(session.ActiveMessengerServerBaseUrl ?? "");
            return endpoint.Length == 0 ? "Server" : $"Server {endpoint}";
        }

        return FormatChannel(chat);
    }

    public static string FormatPanel(string fingerprints, ChatEntity chat) =>
        FormatPanel(fingerprints, chat, session: null);

    public static string FormatPanel(string fingerprints, ChatEntity chat, ChatP2PSession? session)
    {
        var channel = FormatChannel(chat, session);
        return channel.Length == 0 ? fingerprints : fingerprints + Environment.NewLine + "Канал: " + channel;
    }

    public static string FormatKeyChangeWarning(PeerPublicKeyChangedEventArgs e)
    {
        return
            $"Публичный ключ собеседника «{e.PeerNickname}» изменился.\n\n" +
            $"Было: {e.PreviousSafetyNumber}\nСтало: {e.NewSafetyNumber}\n\n" +
            "Если вы не меняли ключ намеренно, это может быть атака «человек посередине».";
    }

    public static string FormatKeyChangeProposal(PeerKeyChangeProposedEventArgs e)
    {
        return
            $"Контакт «{e.PeerNickname}» ({e.PeerNetworkIdShort}) предлагает другой публичный ключ.\n\n" +
            $"Было: {e.PreviousSafetyNumber}\nПредложено: {e.ProposedSafetyNumber}\n\n" +
            "Примите только если вы уверены, что это тот же человек (например после восстановления из .tlp). " +
            "Иначе отклоните — это может быть подмена.";
    }

    public static string FormatServerEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out var uri))
            return (baseUrl ?? "").Trim();
        var port = uri.IsDefaultPort
            ? uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80
            : uri.Port;
        return $"{uri.Host}:{port}";
    }
}
