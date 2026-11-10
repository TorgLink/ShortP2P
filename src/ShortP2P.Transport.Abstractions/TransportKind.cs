namespace ShortP2P.Transport.Abstractions;

/// <summary>
///     Well-known transport kind identifiers for mesh exchange.
///     Stored and compared as <see cref="byte"/> so libraries can introduce additional kinds
///     (define their own <c>const byte</c> values that do not collide with these).
/// </summary>
public static class TransportKind
{
    public const byte Udp = 1;

    public const byte Bluetooth = 2;

    public const byte Infrared = 3;

    /// <summary>Peer discovered via HTTPS messenger server (GetClients), without a direct UDP/BT address.</summary>
    public const byte MessengerServer = 4;

    /// <summary>LoRa / Meshtastic host transport (ShortP2P.LoRa). Must stay aligned with ShortP2P.LoRa.TransportKind.Meshtastic.</summary>
    public const byte Meshtastic = 5;
}
