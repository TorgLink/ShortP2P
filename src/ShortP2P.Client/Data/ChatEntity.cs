using SQLite;

namespace ShortP2P.Client.Data;

[Table("chats")]
public class ChatEntity
{
    [PrimaryKey] [AutoIncrement] public int Id { get; set; }

    [Indexed] public int UserId { get; set; }

    public string PeerNickname { get; set; } = "";

    public string PeerNetworkIdShort { get; set; } = "";

    public string PeerRsaPublicJson { get; set; } = "";

    /// <summary>How the current peer public key was obtained: udp, bluetooth, server, qr, manual.</summary>
    public string? PeerKeySourceKind { get; set; }

    /// <summary>Server BaseUrl when <see cref="PeerKeySourceKind"/> is server.</summary>
    public string? PeerKeySourceDetail { get; set; }

    /// <summary>
    ///     Один или несколько IPv4/IPv6 через запятую; пополняется при новых контактах с пиром (приглашение, поиск,
    ///     ручной ввод).
    /// </summary>
    public string PeerHost { get; set; } = "127.0.0.1";

    public int PeerPort { get; set; } = 17201;

    /// <summary>JSON-массив endpoint'ов пира: UDP/Bluetooth (TransportKind + Data base64).</summary>
    public string? PeerEndpointsJson { get; set; }

    /// <summary>Сериализованный маршрут ретрансляции (первый хоп + цепочка), null/пусто — прямой UDP.</summary>
    public string? RelayRouteBlob { get; set; }

    /// <summary><see cref="Services.ChatDeliveryPath"/> stored as int. 0 = auto.</summary>
    public int DeliveryPath { get; set; }

    /// <summary>True when the peer is a messenger-server bot (BotClientInteraction path).</summary>
    public bool IsBot { get; set; }

    /// <summary>
    /// True after a successful GET /bots classified this chat (bot with key, or confirmed not in catalog).
    /// Stops repeating catalog probes on every outbound send for ordinary human chats.
    /// </summary>
    public bool BotCatalogChecked { get; set; }

    /// <summary>
    /// True after both peers advertised EnableMlKem and completed ML-KEM session verification
    /// (capability exchange + re-established session). Used for the quantum UI mark.
    /// </summary>
    public bool MlKemSessionVerified { get; set; }

    public long UpdatedUtcTicks { get; set; }
}