using System.Net;
using ShortP2P.Auth;
using ShortP2P.Auth.Data;
using ShortP2P.Client.Routing;
using ShortP2P.Client.Services.MessengerServers;
using ShortP2P.Crypto;
using ShortP2P.Discovery;
using ShortP2P.Transport;
using ShortP2P.Transport.Abstractions;

namespace ShortP2P.Client.Services;

/// <summary>
///     Начало чата с пиром из discovery: отправка ChatInvite (UDP / Bluetooth / host transport, например
///     LoRa Kind=5) и ожидание ответного приглашения с ключом.
/// </summary>
public static class LanChatStartFromDiscovery
{
    public static async Task<LanChatStartResult> TryStartAsync(
        DiscoveredLocalPeer peer,
        AuthService auth,
        ChatRepository chats,
        LanChatStartContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var user = auth.CurrentUser;
        if (user == null)
            return LanChatStartResult.Failed("Выполните вход.");

        if (peer.TransportKind == TransportKind.MessengerServer)
            return await TryStartViaMessengerServerAsync(peer, auth, chats, context?.MessengerServers, cancellationToken)
                .ConfigureAwait(false);

        ITransport? hostTransport = null;
        switch (peer.TransportKind)
        {
            case TransportKind.Udp:
                break;
            case TransportKind.Bluetooth:
                if (context?.BluetoothTransport == null)
                    return LanChatStartResult.Failed("Bluetooth transport is unavailable.");
                break;
            default:
                // Host-provided transport (e.g. LoRa/Meshtastic): resolve the outbound transport
                // for the peer's transport address (its mesh address).
                hostTransport = context?.ResolveOutboundTransport?.Invoke(peer.SourceAddress);
                if (hostTransport == null)
                    return LanChatStartResult.Failed(
                        "Транспорт пира не запущен (например, включите LoRa/Meshtastic в настройках).");
                break;
        }

        var idShort = peer.NetworkId.ToShortString();
        var existing = await chats.FindChatByPeerNetworkIdAsync(user.Id, idShort).ConfigureAwait(false);
        if (existing != null)
        {
            if (hostTransport != null)
                return LanChatStartResult.AlreadyExists(existing);

            var seenIp = peer.TransportKind == TransportKind.Bluetooth
                ? BluetoothTransportAddress.ToMacString(peer.SourceAddress.Data)
                : UdpTransportAddress.ToIPEndPoint(peer.SourceAddress).Address.ToString();
            var mergedHost = PeerHostList.WithPrimaryFirst(existing.PeerHost, seenIp);

            if (string.Equals(mergedHost, existing.PeerHost, StringComparison.Ordinal))
                return LanChatStartResult.AlreadyExists(existing);

            await chats.UpdateChatP2pRouteAsync(existing.Id, mergedHost, existing.PeerPort, existing.RelayRouteBlob)
                .ConfigureAwait(false);
            chats.NotifyChatListChanged();
            var fresh = await chats.GetChatAsync(existing.Id).ConfigureAwait(false);
            return LanChatStartResult.AlreadyExists(fresh ?? existing);
        }

        var dest = hostTransport != null || peer.TransportKind == TransportKind.Bluetooth
            ? peer.SourceAddress
            : UdpTransportAddress.FromIPEndPoint(
                new IPEndPoint(UdpTransportAddress.ToIPEndPoint(peer.SourceAddress).Address,
                    ChatInviteCodec.InviteUdpPort));

        var btMac = context?.Settings?.EnableBluetoothTransport == false
            ? null
            : context?.BluetoothAdapterMac;
        var host = InviteHostsBuilder.BuildCommaSeparated(
            context?.Settings,
            btMac,
            user.NetworkIdShort,
            TimeSpan.FromSeconds(10));
        var nid = CompressedNetworkId.FromShortString(user.NetworkIdShort);
        var invite = ChatInviteCodec.Build(user.Nickname, nid,
            RsaKeySerializer.SerializePublic(auth.GetCurrentPublicKey()), host, ChatInviteCodec.InviteUdpPort);

        if (hostTransport != null)
        {
            // One ShortP2P datagram to the peer's mesh address; the transport chunks and retries it.
            // Failure to transmit (e.g. LoRa region unset, port closed) is a user-facing condition.
            try
            {
                await hostTransport.SendAsync(invite, dest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return LanChatStartResult.Failed(ex.Message);
            }
        }
        else
        {
            var sharedFactory = context?.UdpTransportFactory;
            if (sharedFactory != null)
            {
                if (peer.TransportKind == TransportKind.Udp)
                {
                    var udp = sharedFactory.Acquire(IPAddress.Any, ChatInviteCodec.InviteUdpPort);
                    try
                    {
                        await udp.StartAsync(cancellationToken).ConfigureAwait(false);
                        await udp.SendAsync(invite, dest, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        try
                        {
                            await sharedFactory.ReleaseAsync(udp, cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
                else
                {
                    var bt = context!.BluetoothTransport!;
                    await bt.StartAsync(cancellationToken).ConfigureAwait(false);
                    await bt.SendAsync(invite, dest, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                if (context?.StopInviteListenerAsync != null)
                    await context.StopInviteListenerAsync(cancellationToken).ConfigureAwait(false);
                var udp = UdpTransport.CreateUdpTransport(IPAddress.Any, ChatInviteCodec.InviteUdpPort);
                try
                {
                    await udp.StartAsync(cancellationToken).ConfigureAwait(false);
                    if (peer.TransportKind == TransportKind.Bluetooth)
                    {
                        var bt = context!.BluetoothTransport!;
                        await bt.StartAsync(cancellationToken).ConfigureAwait(false);
                        await bt.SendAsync(invite, dest, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await udp.SendAsync(invite, dest, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    try
                    {
                        await udp.StopAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignore
                    }

                    if (context?.EnsureInviteListenerAsync != null)
                    {
                        var u = auth.CurrentUser;
                        if (u != null)
                            await context.EnsureInviteListenerAsync(u, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        for (var i = 0; i < 50; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            var chat = await chats.FindChatByPeerNetworkIdAsync(user.Id, idShort).ConfigureAwait(false);
            if (chat != null)
                return LanChatStartResult.Created(chat);
        }

        return LanChatStartResult.WaitingForPeer();
    }

    private static async Task<LanChatStartResult> TryStartViaMessengerServerAsync(
        DiscoveredLocalPeer peer,
        AuthService auth,
        ChatRepository chats,
        MessengerServerSyncService? servers,
        CancellationToken cancellationToken)
    {
        var user = auth.CurrentUser;
        if (user == null)
            return LanChatStartResult.Failed("Выполните вход.");

        var idShort = peer.NetworkId.ToShortString();
        var existing = await chats.FindChatByPeerNetworkIdAsync(user.Id, idShort).ConfigureAwait(false);
        if (servers == null)
            return LanChatStartResult.Failed("Messenger-серверы не подключены.");

        if (peer.IsBot)
        {
            var botKey = peer.MessagePublicKey?.Trim() ?? "";
            if (botKey.Length == 0)
                return LanChatStartResult.Failed("У бота нет публичного ключа сообщений (обновите каталог: Скан).");

            if (existing != null)
            {
                await chats.TryApplyBotCatalogKeyAsync(existing.Id, botKey).ConfigureAwait(false);
                var fresh = await chats.GetChatAsync(existing.Id).ConfigureAwait(false);
                return LanChatStartResult.AlreadyExists(fresh ?? existing);
            }

            var botNick = string.IsNullOrWhiteSpace(peer.Nickname) ? idShort : peer.Nickname.Trim();
            var botChat = await chats.AddChatAsync(
                    user.Id,
                    botNick,
                    idShort,
                    botKey,
                    idShort,
                    user.DataUdpPort,
                    remote: false,
                    keySource: PeerKeySource.Server("bots"),
                    isBot: true)
                .ConfigureAwait(false);
            return LanChatStartResult.Created(botChat);
        }

        try
        {
            await servers.PublishChatRequestAsync(idShort, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return LanChatStartResult.Failed(ex.Message);
        }

        if (existing != null)
            return LanChatStartResult.AlreadyExists(existing);

        var nick = string.IsNullOrWhiteSpace(peer.Nickname) ? idShort : peer.Nickname.Trim();
        var chat = await chats.AddChatAsync(
                user.Id,
                nick,
                idShort,
                "",
                idShort,
                user.DataUdpPort,
                remote: false,
                keySource: PeerKeySource.Server("invite"))
            .ConfigureAwait(false);

        return LanChatStartResult.Created(chat);
    }
}
