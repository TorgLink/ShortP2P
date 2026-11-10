using System.Buffers.Binary;
using System.Text;
using ShortP2P.Auth.Data;
using ShortP2P.Discovery.Transceivers;
using ShortP2P.Transport;
using ShortP2P.Transport.Abstractions;

namespace ShortP2P.Discovery;

/// <summary>
///     Единый модуль presence / discovery ping (кадр 0x31, порт <see cref="UdpPort" />): сериализация
///     <see cref="Build" /> / <see cref="TryParse" /> и приёмопередатчик <see cref="Transceiver" /> поверх
///     <see cref="ITransport" /> (UDP, BLE и т.д.).
///     Формат: [0]=frame, [1..12]=network id, [13..14]=длина ника, [15..]=UTF-8 ник, uint16 BE dataUdpPort,
///     [+1]=<see cref="LinkTechnologyPreset" /> (опционально), [+2]=uint16 BE <see cref="PresencePeerCapabilities" />
///     (опционально, на будущее).
///     Совместимость: 13 байт только id; 15+nick — ник без порта; без байта скорости —
///     <see cref="LinkTechnologyPreset.Unlimited" />;
///     без двух байт маски — считается только Messaging (<see cref="PresencePeerCapabilities.Chat" />) у отправителя
///     legacy-клиента.
///     Полный перечень ролей узла — README ShortP2P.Discovery, раздел «Узел и возможности».
/// </summary>
public static class PresencePingCodec
{
    /// <summary>Локальный и удалённый UDP-порт только для discovery/presence ping.</summary>
    public const int UdpPort = 17501;

    private const byte FramePresencePing = 0x31;

    private const int MaxNicknameUtf8Bytes = 512;

    /// <summary>Если в пакете нет поля порта (старые клиенты).</summary>
    public const int DefaultDataUdpPort = 17500;

    public static byte[] Build(CompressedNetworkId networkId, string nickname, int dataUdpPort,
        LinkTechnologyPreset advertisedLink = LinkTechnologyPreset.Unlimited,
        PresencePeerCapabilities advertisedCapabilities = PresencePeerCapabilities.Chat)
    {
        nickname ??= string.Empty;
        if (dataUdpPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(dataUdpPort));

        var nickBytes = Encoding.UTF8.GetBytes(nickname.Trim());
        if (nickBytes.Length > MaxNicknameUtf8Bytes)
            nickBytes = nickBytes.AsSpan(0, MaxNicknameUtf8Bytes).ToArray();

        const int trailerAfterPort = 1 + 2; // LinkTechnology + capabilities BE
        var buf = new byte[1 + CompressedNetworkId.WireLength + 2 + nickBytes.Length + 2 + trailerAfterPort];
        buf[0] = FramePresencePing;
        if (!networkId.TryWriteBytes(buf.AsSpan(1, CompressedNetworkId.WireLength)))
            throw new InvalidOperationException("Failed to write network id.");
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(13, 2), (ushort)nickBytes.Length);
        nickBytes.CopyTo(buf.AsSpan(15));
        var portOff = 15 + nickBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(portOff, 2), (ushort)dataUdpPort);
        buf[portOff + 2] = (byte)advertisedLink;
        var cap = (ushort)((ushort)advertisedCapabilities & (ushort)PresencePeerCapabilities.AllDefined);
        cap |= (ushort)PresencePeerCapabilities.Chat;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(portOff + 3, 2), cap);
        return buf;
    }

    public static bool TryParse(ReadOnlySpan<byte> datagram, out CompressedNetworkId networkId, out string nickname,
        out int dataUdpPort, out LinkTechnologyPreset advertisedLink,
        out PresencePeerCapabilities advertisedCapabilities)
    {
        networkId = CompressedNetworkId.Empty;
        nickname = "";
        dataUdpPort = DefaultDataUdpPort;
        advertisedLink = LinkTechnologyPreset.Unlimited;
        advertisedCapabilities = PresencePeerCapabilities.Chat;

        if (datagram.Length < 1 + CompressedNetworkId.WireLength || datagram[0] != FramePresencePing)
            return false;

        networkId = CompressedNetworkId.FromWireBytes(datagram.Slice(1, CompressedNetworkId.WireLength));
        if (datagram.Length == 1 + CompressedNetworkId.WireLength)
            return true;

        if (datagram.Length < 15)
            return false;

        var nickLen = BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(13, 2));
        if (nickLen > MaxNicknameUtf8Bytes)
            return false;

        if (datagram.Length < 15 + nickLen)
            return false;

        try
        {
            nickname = Utf8Span.GetString(datagram.Slice(15, nickLen));
        }
        catch
        {
            return false;
        }

        if (datagram.Length == 15 + nickLen)
            return true;

        var afterNick = 15 + nickLen;
        if (datagram.Length == afterNick + 2)
        {
            dataUdpPort = BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(afterNick, 2));
            return dataUdpPort is >= 1 and <= 65535;
        }

        if (datagram.Length < afterNick + 3)
            return false;

        dataUdpPort = BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(afterNick, 2));
        if (dataUdpPort is < 1 or > 65535)
            return false;
        var lt = (LinkTechnologyPreset)datagram[afterNick + 2];
        if (!Enum.IsDefined(typeof(LinkTechnologyPreset), lt))
            return false;
        advertisedLink = lt;

        if (datagram.Length == afterNick + 3)
            return true;

        if (datagram.Length < afterNick + 5)
            return false;

        var raw = BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(afterNick + 3, 2));
        advertisedCapabilities =
            (PresencePeerCapabilities)(raw & (ushort)PresencePeerCapabilities.AllDefined);
        advertisedCapabilities |= PresencePeerCapabilities.Chat;
        return true;
    }

    /// <summary>
    ///     Тот же presence-пинг (0x31): отправка через <see cref="ITransport" />, разбор входящих через
    ///     <see cref="HandleIncoming" />.
    /// </summary>
    public sealed class Transceiver(ITransport transport, int udpPort = UdpPort) : IBroadcastTransceiver<PingMessage>
    {
        private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        private bool _started;

        public event EventHandler<PingMessage>? GotData;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            _started = true;
            return default;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            _started = false;
            return default;
        }

        public async ValueTask SendAsync(PingMessage message, TransportAddress destination,
            CancellationToken cancellationToken = default)
        {
            Require.NotNull(message);
            Require.NotNull(destination);
            var packet = BuildPacket(message);
            await _transport.SendAsync(packet, destination, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask SendBroadcastAsync(PingMessage message, CancellationToken cancellationToken = default)
        {
            Require.NotNull(message);
            if (_transport.Kind != TransportKind.Udp)
                return;
            var packet = BuildPacket(message);
            foreach (var ep in LanBroadcastHelper.GetIpv4BroadcastEndpoints(udpPort))
                try
                {
                    await _transport.SendAsync(packet, UdpTransportAddress.FromIPEndPoint(ep), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // best-effort
                }
        }

        public ValueTask DisposeAsync()
        {
            return StopAsync();
        }

        private static byte[] BuildPacket(PingMessage message)
        {
            if (!message.RawPayload.IsEmpty)
                return message.RawPayload.ToArray();
            return Build(message.PeerNetworkId, message.Nickname, message.PeerDataUdpPort,
                message.AdvertisedLink, message.AdvertisedCapabilities);
        }

        /// <summary>Передаёт входящий пакет; вызывается внешним циклом чтения транспорта.</summary>
        public void HandleIncoming(TransportReceiveMessage msg)
        {
            if (!_started)
                return;
            if (!TryParse(msg.Payload.Span, out var nid, out var nick, out var dataPort,
                    out var link, out var caps))
                return;
            var ping = new PingMessage(nid, nick, dataPort, link, caps, msg.Payload, msg.RemoteAddress);
            try
            {
                GotData?.Invoke(this, ping);
            }
            catch
            {
                // подписчик не должен ронять вызывающий цикл
            }
        }
    }
}