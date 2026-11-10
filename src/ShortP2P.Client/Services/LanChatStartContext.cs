using ShortP2P.Auth.Data;
using ShortP2P.Client.Services.MessengerServers;
using ShortP2P.Discovery;
using ShortP2P.Transport;
using ShortP2P.Transport.Abstractions;

namespace ShortP2P.Client.Services;

/// <summary>Optional runtime pieces for LAN chat start (UDP invite / messenger servers).</summary>
public sealed class LanChatStartContext
{
    public MessengerServerSyncService? MessengerServers { get; init; }
    public IUdpTransportFactory? UdpTransportFactory { get; init; }
    public P2pRoutingSettings? Settings { get; init; }
    public ITransport? BluetoothTransport { get; init; }
    public string? BluetoothAdapterMac { get; init; }
    public Func<CancellationToken, Task>? StopInviteListenerAsync { get; init; }
    public Func<UserEntity, CancellationToken, Task>? EnsureInviteListenerAsync { get; init; }

    /// <summary>
    ///     Resolves the outbound transport for a peer on a host-provided kind (e.g. LoRa/Meshtastic);
    ///     null when that transport is not running. Used for invite delivery beyond UDP/Bluetooth.
    /// </summary>
    public Func<TransportAddress, ITransport?>? ResolveOutboundTransport { get; init; }
}
