using System.Diagnostics;
using System.Net;
using ShortP2P.Auth;
using ShortP2P.Auth.Data;
using ShortP2P.Client.Routing;
using ShortP2P.Crypto;
using ShortP2P.Discovery;
using ShortP2P.Transport;
using ShortP2P.Transport.Abstractions;

namespace ShortP2P.Client.Services;

/// <summary>
///     Добавляет чат с инициатором в БД и уведомляет <see cref="ChatRepository.ChatListChanged" />; ответный invite
///     уходит в фоне, чтобы список чатов обновлялся сразу и приёмник не ждал отправки.
///     V2: NetworkId must match identity public key; first contact is TOFU; key change requires explicit accept.
/// </summary>
public static class IncomingChatInviteHandler
{
    /// <param name="repo"></param>
    /// <param name="sendInviteReplyAsync">
    ///     Если задано, отправляет пиру обратное приглашение с нашим ключом (новый чат или обновление маршрута).
    /// </param>
    /// <param name="sourceAddress"></param>
    /// <param name="routingSettings"></param>
    /// <param name="bluetoothAdapterMac"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="datagram"></param>
    /// <param name="auth"></param>
    public static async Task TryAcceptAsync(ReadOnlyMemory<byte> datagram, AuthService auth, ChatRepository repo,
        Func<ReadOnlyMemory<byte>, TransportAddress, CancellationToken, Task>? sendInviteReplyAsync,
        TransportAddress? sourceAddress = null,
        P2pRoutingSettings? routingSettings = null,
        string? bluetoothAdapterMac = null,
        CancellationToken cancellationToken = default)
    {
        if (!ChatInviteCodec.TryParse(datagram.Span, out var peerId, out var nick, out var pubJson, out var host,
                out var port))
            return;

        var user = auth.CurrentUser;
        if (user == null)
            return;

        if (peerId == CompressedNetworkId.FromShortString(user.NetworkIdShort))
            return;

        if (!IdentityKeySerializer.TryDeserializePublic(pubJson, out var peerPub))
            return;

        if (!peerId.MatchesIdentityPublicKey(peerPub))
        {
            Trace.TraceWarning(
                $"[Invite] rejected: NetworkId {peerId.ToShortString()} does not match identity public key");
            return;
        }

        var idShort = peerId.ToShortString();
        var blocked = await repo.IsPeerBlockedAsync(user.Id, idShort, cancellationToken).ConfigureAwait(false);
        var effectiveHost = ResolvePeerHost(host, sourceAddress);
        cancellationToken.ThrowIfCancellationRequested();
        var existing = await repo.FindChatByPeerNetworkIdAsync(user.Id, idShort).ConfigureAwait(false);
        var keySource = sourceAddress?.Kind == TransportKind.Bluetooth
            ? PeerKeySource.Bluetooth()
            : PeerKeySource.Udp();
        if (existing != null)
        {
            var previousKey = existing.PeerRsaPublicJson;
            var keyChanged = !string.IsNullOrWhiteSpace(previousKey) &&
                             !IdentityKeySerializer.PublicKeyJsonEquals(previousKey, pubJson);

            if (keyChanged)
            {
                // Do not overwrite the pinned key. Stash a pending change for an explicit UI accept.
                await repo.SetPendingPeerKeyChangeAsync(existing.Id, pubJson, nick, effectiveHost, keySource)
                    .ConfigureAwait(false);
                return;
            }

            var mergedHost = PeerHostList.WithPrimaryFirst(existing.PeerHost, effectiveHost);
            var chatPeerPort = existing.PeerPort is < 1 or > 65535 || existing.PeerPort == ChatInviteCodec.InviteUdpPort
                ? PresencePingCodec.DefaultDataUdpPort
                : existing.PeerPort;
            await repo.UpdateChatP2pRouteAsync(existing.Id, mergedHost, chatPeerPort, null, pubJson, keySource)
                .ConfigureAwait(false);
            await repo.TryUpdatePeerNicknameAsync(existing.Id, nick).ConfigureAwait(false);
            existing.PeerHost = mergedHost;
            existing.PeerPort = chatPeerPort;
            existing.RelayRouteBlob = null;
            existing.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            if (!blocked)
                repo.NotifyChatListChanged();
            if (!blocked)
            {
                ScheduleInviteReply(auth, user, host, port, sourceAddress, sendInviteReplyAsync, routingSettings,
                    bluetoothAdapterMac);
            }

            return;
        }

        // TOFU: first invite for this NetworkId pins the key.
        await repo.AddChatAsync(user.Id, nick, idShort, pubJson, effectiveHost, PresencePingCodec.DefaultDataUdpPort,
                remote: true, keySource: keySource).ConfigureAwait(false);

        if (!blocked)
        {
            ScheduleInviteReply(auth, user, host, port, sourceAddress, sendInviteReplyAsync, routingSettings,
                bluetoothAdapterMac);
        }
    }

    private static void ScheduleInviteReply(AuthService auth, UserEntity user, string inviteHostList,
        int invitePacketPort, TransportAddress? sourceAddress,
        Func<ReadOnlyMemory<byte>, TransportAddress, CancellationToken, Task>? sendInviteReplyAsync,
        P2pRoutingSettings? routingSettings = null,
        string? bluetoothAdapterMac = null)
    {
        if (sendInviteReplyAsync == null)
            return;
        _ = TrySendInviteReplyAsync(auth, user, inviteHostList, invitePacketPort, sourceAddress, sendInviteReplyAsync,
                routingSettings, bluetoothAdapterMac, CancellationToken.None)
            .ContinueWith(
                static t =>
                {
                    if (t.IsFaulted)
                        Trace.TraceWarning(
                            "[Invite] reply send failed: " + (t.Exception?.GetBaseException().Message ?? ""));
                },
                TaskScheduler.Default);
    }

    private static async Task TrySendInviteReplyAsync(AuthService auth, UserEntity user, string inviteHostList,
        int invitePacketPort, TransportAddress? sourceAddress,
        Func<ReadOnlyMemory<byte>, TransportAddress, CancellationToken, Task>? sendInviteReplyAsync,
        P2pRoutingSettings? routingSettings,
        string? bluetoothAdapterMac,
        CancellationToken cancellationToken)
    {
        if (sendInviteReplyAsync == null)
            return;
        var myHost = InviteHostsBuilder.BuildCommaSeparated(
            routingSettings,
            routingSettings?.EnableBluetoothTransport == false ? null : bluetoothAdapterMac,
            user.NetworkIdShort,
            TimeSpan.FromSeconds(2));
        var nid = CompressedNetworkId.FromShortString(user.NetworkIdShort);
        var reply = ChatInviteCodec.Build(user.Nickname, nid,
            IdentityKeySerializer.SerializePublic(auth.GetCurrentPublicKey()), myHost, ChatInviteCodec.InviteUdpPort);
        var parseHost = PeerHostList.PrimaryHost(inviteHostList);
        var back = sourceAddress ?? UdpTransportAddress.FromIPEndPoint(
            new IPEndPoint(IPAddress.Parse(parseHost), invitePacketPort));
        await sendInviteReplyAsync(reply, back, cancellationToken).ConfigureAwait(false);
    }

    private static string ResolvePeerHost(string inviteHost, TransportAddress? sourceAddress)
    {
        switch (sourceAddress?.Kind)
        {
            case TransportKind.Bluetooth:
                return BluetoothTransportAddress.ToMacString(sourceAddress.Data);
            case TransportKind.Udp:
            {
                var seenIp = UdpTransportAddress.ToIPEndPoint(sourceAddress).Address.ToString();
                return PeerHostList.WithPrimaryFirst(inviteHost, seenIp);
            }
            default:
                return inviteHost;
        }
    }
}
