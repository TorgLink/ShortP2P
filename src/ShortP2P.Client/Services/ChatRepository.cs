using System.Net;
using ShortP2P.Auth.Data;
using ShortP2P.Client.Data;
using ShortP2P.Crypto;
using ShortP2P.Transport;
using ShortP2P.Transport.Abstractions;
using SQLite;

namespace ShortP2P.Client.Services;

public sealed class ChatRepository(AppDatabase appDatabase, PeerBlacklist? blacklist = null)
{
    private readonly SemaphoreSlim _addChatGate = new(1, 1);
    private readonly AppDatabase _db = appDatabase ?? throw new global::System.ArgumentNullException(nameof(appDatabase));
    private int _seenPruneCounter;

    /// <summary>Список чатов на главном экране: обновить после входящего приглашения и т.п.</summary>
    public event EventHandler? ChatListChanged;

    /// <summary>Новое сообщение записано в БД (входящее или исходящее).</summary>
    public event EventHandler<ChatMessageAppendedEventArgs>? ChatMessageAppended;

    /// <summary>Изменился статус доставки исходящего сообщения (квитанция и т.п.).</summary>
    public event EventHandler<ChatMessageAppendedEventArgs>? ChatMessageDeliveryChanged;

    /// <summary>В БД вставлен новый чат (не обновление существующего).</summary>
    public event EventHandler<ChatCreatedEventArgs>? ChatCreated;

    /// <summary>
    /// Genuine new incoming invite (first acceptance of a ChatRequest). UI may open the chat.
    /// Not raised for duplicate/failover re-delivery of an already-known peer.
    /// </summary>
    public event EventHandler<ChatCreatedEventArgs>? IncomingChatInvite;

    /// <summary>Сохранённый публичный ключ пира заменён другим (возможный MITM).</summary>
    public event EventHandler<PeerPublicKeyChangedEventArgs>? PeerPublicKeyChanged;

    public void NotifyChatListChanged() =>
        RaiseEvent(ChatListChanged, EventArgs.Empty);

    public async Task NotifyIncomingChatInviteAsync(int chatId, CancellationToken cancellationToken = default)
    {
        var chat = await GetChatAsync(chatId).ConfigureAwait(false);
        if (chat != null &&
            await IsPeerBlockedAsync(chat.UserId, chat.PeerNetworkIdShort, cancellationToken).ConfigureAwait(false))
            return;
        RaiseEvent(IncomingChatInvite, new ChatCreatedEventArgs(chatId, remote: true));
    }

    public async Task<bool> IsPeerBlockedAsync(int userId, string? peerNetworkId,
        CancellationToken cancellationToken = default)
    {
        if (blacklist == null)
            return false;
        await blacklist.EnsureLoadedAsync(userId, cancellationToken).ConfigureAwait(false);
        return blacklist.IsBlocked(userId, peerNetworkId);
    }

    public async Task<bool> IsChatFromBlockedPeerAsync(int chatId, CancellationToken cancellationToken = default)
    {
        var chat = await GetChatAsync(chatId).ConfigureAwait(false);
        if (chat == null)
            return false;
        return await IsPeerBlockedAsync(chat.UserId, chat.PeerNetworkIdShort, cancellationToken).ConfigureAwait(false);
    }

    private async Task NotifyChatListChangedUnlessBlockedAsync(int userId, string? peerNetworkId)
    {
        if (await IsPeerBlockedAsync(userId, peerNetworkId).ConfigureAwait(false))
            return;
        NotifyChatListChanged();
    }

    private async Task RaiseChatMessageAppendedAsync(int chatId, bool outgoing, ChatEntity? chat)
    {
        if (!outgoing && chat != null &&
            await IsPeerBlockedAsync(chat.UserId, chat.PeerNetworkIdShort).ConfigureAwait(false))
            return;
        RaiseEvent(ChatMessageAppended, new ChatMessageAppendedEventArgs(chatId, outgoing));
    }

    /// <summary>
    /// Invoke multicast handlers one-by-one so a disposed UI subscriber cannot abort chat create
    /// or block other listeners (common after WinForms logout/login).
    /// </summary>
    private void RaiseEvent<TEventArgs>(EventHandler<TEventArgs>? handlers, TEventArgs args)
        where TEventArgs : EventArgs
    {
        if (handlers == null)
            return;
        
        foreach (var @delegate in handlers.GetInvocationList())
        {
            var handler = (EventHandler<TEventArgs>)@delegate;
            try
            {
                handler(this, args);
            }
            catch
            {
                // UI / listener failures must not roll back DB writes.
            }
        }
    }

    private void RaiseEvent(EventHandler? handlers, EventArgs args)
    {
        if (handlers == null)
            return;
        foreach (var @delegate in handlers.GetInvocationList())
        {
            var handler = (EventHandler)@delegate;
            try
            {
                handler(this, args);
            }
            catch
            {
                // UI / listener failures must not roll back DB writes.
            }
        }
    }

    public async Task<IReadOnlyList<ChatEntity>> ListChatsAsync(int userId)
    {
        var conn = await _db.GetConnectionAsync().ConfigureAwait(false);
        var rows = await conn.Table<ChatEntity>()
            .Where(c => c.UserId == userId)
            .ToListAsync()
            .ConfigureAwait(false);

        // Persist merge of duplicate peers (same canonical network id) so offline history stays on one row.
        rows = await DeduplicateChatsInDbAsync(conn, rows, notify: false).ConfigureAwait(false);

        return rows
            .GroupBy(CanonicalPeerNetworkIdKey, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(c => c.UpdatedUtcTicks).First())
            .OrderByDescending(c => c.UpdatedUtcTicks)
            .ToList();
    }

    public async Task<ChatEntity?> GetChatAsync(int chatId)
    {
        var conn = await _db.GetReadConnectionAsync().ConfigureAwait(false);
        return await conn.FindAsync<ChatEntity>(chatId).ConfigureAwait(false);
    }

    public async Task<ChatEntity> AddChatAsync(int userId, string peerNickname, string peerNetworkIdShort,
        string peerRsaPublicJson, string peerHost, int peerPort, bool remote = false,
        PeerKeySource? keySource = null, bool isBot = false)
    {
        await _addChatGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var idShort = CanonicalPeerNetworkId(peerNetworkIdShort);
            if (idShort.Length == 0)
                throw new global::System.ArgumentException("Peer network id is required.", nameof(peerNetworkIdShort));

            var existing = await FindChatByPeerNetworkIdAsync(userId, idShort).ConfigureAwait(false);
            var preferredNick = PreferPeerNickname(peerNickname, idShort);
            if (existing != null)
            {
                var mergedHost = PeerHostList.MergeAppend(existing.PeerHost, peerHost);
                var mergedEndpoints = MergePeerEndpoints(existing, peerHost, peerPort);
                var newPub = peerRsaPublicJson?.Trim();
                var pubChanged = !string.IsNullOrEmpty(newPub) &&
                                 !SafetyNumber.PublicKeyJsonEquals(existing.PeerRsaPublicJson, newPub);
                var nickChanged = ShouldReplacePeerNickname(existing.PeerNickname, preferredNick, idShort);
                var sourceChanged = keySource.HasValue &&
                                    (!string.Equals(existing.PeerKeySourceKind, keySource.Value.Kind,
                                         StringComparison.Ordinal) ||
                                     !string.Equals(existing.PeerKeySourceDetail ?? "",
                                         keySource.Value.Detail ?? "", StringComparison.Ordinal));
                var botFlagChanged = isBot && !existing.IsBot;
                var changed =
                    !string.Equals(mergedHost, existing.PeerHost, StringComparison.Ordinal) ||
                    existing.PeerPort != peerPort ||
                    !string.Equals(mergedEndpoints, existing.PeerEndpointsJson ?? "", StringComparison.Ordinal) ||
                    pubChanged ||
                    nickChanged ||
                    botFlagChanged ||
                    (pubChanged && sourceChanged) ||
                    (existing.PeerKeySourceKind is null or "" && keySource.HasValue) ||
                    !string.IsNullOrEmpty(existing.RelayRouteBlob);

                if (!changed)
                    return existing;

                await UpdateChatP2pRouteAsync(existing.Id, mergedHost, peerPort, null, peerRsaPublicJson,
                        pubChanged || string.IsNullOrWhiteSpace(existing.PeerKeySourceKind) ? keySource : null)
                    .ConfigureAwait(false);
                existing.PeerHost = mergedHost;
                existing.PeerPort = peerPort;
                existing.PeerEndpointsJson = mergedEndpoints;
                if (newPub != null && newPub.Length > 0)
                    existing.PeerRsaPublicJson = newPub;
                if (nickChanged)
                    existing.PeerNickname = preferredNick;
                if (isBot)
                    existing.IsBot = true;
                if (keySource.HasValue &&
                    (pubChanged || string.IsNullOrWhiteSpace(existing.PeerKeySourceKind)))
                {
                    existing.PeerKeySourceKind = keySource.Value.Kind;
                    existing.PeerKeySourceDetail = keySource.Value.Detail;
                }

                existing.RelayRouteBlob = null;
                existing.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                if (nickChanged || botFlagChanged)
                {
                    var connUpdate = await _db.GetConnectionAsync().ConfigureAwait(false);
                    await connUpdate.UpdateAsync(existing).ConfigureAwait(false);
                }

                await NotifyChatListChangedUnlessBlockedAsync(userId, idShort).ConfigureAwait(false);
                return existing;
            }

            var conn = await _db.GetConnectionAsync();
            var chat = new ChatEntity
            {
                UserId = userId,
                PeerNickname = preferredNick,
                PeerNetworkIdShort = idShort,
                PeerRsaPublicJson = peerRsaPublicJson.Trim(),
                PeerHost = peerHost.Trim(),
                PeerPort = peerPort,
                PeerEndpointsJson = MergePeerEndpoints(null, peerHost, peerPort),
                PeerKeySourceKind = keySource?.Kind,
                PeerKeySourceDetail = keySource?.Detail,
                IsBot = isBot,
                UpdatedUtcTicks = DateTime.UtcNow.Ticks
            };
            await conn.InsertAsync(chat);
            if (!await IsPeerBlockedAsync(userId, idShort).ConfigureAwait(false))
            {
                NotifyChatListChanged();
                RaiseEvent(ChatCreated, new ChatCreatedEventArgs(chat.Id, remote));
            }

            return chat;
        }
        finally
        {
            _addChatGate.Release();
        }
    }

    public async Task<ChatEntity?> FindChatByPeerNetworkIdAsync(int userId, string peerNetworkIdShort)
    {
        var id = CanonicalPeerNetworkId(peerNetworkIdShort);
        if (id.Length == 0)
            return null;

        var conn = await _db.GetConnectionAsync().ConfigureAwait(false);
        var list = await conn.Table<ChatEntity>()
            .Where(c => c.UserId == userId)
            .ToListAsync()
            .ConfigureAwait(false);
        var matches = list.Where(c => PeerNetworkIdsEqual(c.PeerNetworkIdShort, id)).ToList();
        
        switch (matches.Count)
        {
            case 0:
                return null;
            case 1:
            {
                var only = matches[0];
                if (string.Equals(only.PeerNetworkIdShort, id, StringComparison.Ordinal)) return only;
                only.PeerNetworkIdShort = id;
                only.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                await conn.UpdateAsync(only).ConfigureAwait(false);

                return only;
            }
            default:
            {
                var merged = await DeduplicateChatsInDbAsync(conn, matches, notify: true).ConfigureAwait(false);
                return merged.OrderByDescending(c => c.UpdatedUtcTicks).FirstOrDefault();
            }
        }
    }

    /// <summary>Canonical base64url network id used as the chat list key.</summary>
    public static string CanonicalPeerNetworkId(string? peerNetworkIdShort)
    {
        var raw = peerNetworkIdShort?.Trim() ?? "";
        if (raw.Length == 0)
            return "";
        return CompressedNetworkId.TryParseShortString(raw, out var parsed) && !parsed.IsEmpty
            ? parsed.ToShortString()
            : raw;
    }

    public static bool PeerNetworkIdsEqual(string? a, string? b)
    {
        var ca = CanonicalPeerNetworkId(a);
        var cb = CanonicalPeerNetworkId(b);
        if (ca.Length == 0 || cb.Length == 0)
            return string.Equals(a?.Trim(), b?.Trim(), StringComparison.Ordinal);
        return string.Equals(ca, cb, StringComparison.Ordinal);
    }

    private static string CanonicalPeerNetworkIdKey(ChatEntity chat) =>
        CanonicalPeerNetworkId(chat.PeerNetworkIdShort);

    /// <summary>
    /// Merges chats that represent the same peer into one DB row (keeps the row with the most messages).
    /// </summary>
    private async Task<List<ChatEntity>> DeduplicateChatsInDbAsync(SQLiteAsyncConnection conn,
        List<ChatEntity> rows, bool notify)
    {
        if (rows.Count <= 1)
        {
            if (rows.Count != 1) return rows;
            var single = rows[0];
            var canonical = CanonicalPeerNetworkId(single.PeerNetworkIdShort);
            if (canonical.Length <= 0 ||
                string.Equals(single.PeerNetworkIdShort, canonical, StringComparison.Ordinal)) return rows;
            
            single.PeerNetworkIdShort = canonical;
            single.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            await conn.UpdateAsync(single).ConfigureAwait(false);

            return rows;
        }

        var result = new List<ChatEntity>(rows.Count);
        var changed = false;
        foreach (var group in rows.GroupBy(CanonicalPeerNetworkIdKey, StringComparer.Ordinal))
        {
            var members = group.ToList();
            if (members.Count == 1)
            {
                var single = members[0];
                var canonical = group.Key;
                if (canonical.Length > 0 &&
                    !string.Equals(single.PeerNetworkIdShort, canonical, StringComparison.Ordinal))
                {
                    single.PeerNetworkIdShort = canonical;
                    single.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                    await conn.UpdateAsync(single).ConfigureAwait(false);
                    changed = true;
                }

                result.Add(single);
                continue;
            }

            var keeper = await PickChatToKeepAsync(conn, members).ConfigureAwait(false);
            var canonicalId = group.Key.Length > 0 ? group.Key : CanonicalPeerNetworkId(keeper.PeerNetworkIdShort);
            foreach (var orphan in members.Where(c => c.Id != keeper.Id))
            {
                await conn.ExecuteAsync("UPDATE messages SET ChatId = ? WHERE ChatId = ?", keeper.Id, orphan.Id)
                    .ConfigureAwait(false);
                keeper.PeerHost = PeerHostList.MergeAppend(keeper.PeerHost, orphan.PeerHost);
                if (orphan.PeerPort > 0)
                    keeper.PeerPort = orphan.PeerPort;
                keeper.PeerEndpointsJson = MergePeerEndpointsJson(keeper.PeerEndpointsJson, orphan.PeerEndpointsJson);
                if (!string.IsNullOrWhiteSpace(orphan.PeerRsaPublicJson) &&
                    (string.IsNullOrWhiteSpace(keeper.PeerRsaPublicJson) ||
                     orphan.UpdatedUtcTicks >= keeper.UpdatedUtcTicks))
                {
                    keeper.PeerRsaPublicJson = orphan.PeerRsaPublicJson;
                    if (!string.IsNullOrWhiteSpace(orphan.PeerKeySourceKind))
                    {
                        keeper.PeerKeySourceKind = orphan.PeerKeySourceKind;
                        keeper.PeerKeySourceDetail = orphan.PeerKeySourceDetail;
                    }
                }

                if (string.IsNullOrWhiteSpace(keeper.PeerKeySourceKind) &&
                    !string.IsNullOrWhiteSpace(orphan.PeerKeySourceKind))
                {
                    keeper.PeerKeySourceKind = orphan.PeerKeySourceKind;
                    keeper.PeerKeySourceDetail = orphan.PeerKeySourceDetail;
                }
                if (ShouldReplacePeerNickname(keeper.PeerNickname, orphan.PeerNickname, canonicalId))
                    keeper.PeerNickname = PreferPeerNickname(orphan.PeerNickname, canonicalId);
                if (string.IsNullOrWhiteSpace(keeper.RelayRouteBlob) &&
                    !string.IsNullOrWhiteSpace(orphan.RelayRouteBlob))
                    keeper.RelayRouteBlob = orphan.RelayRouteBlob;
                await conn.DeleteAsync(orphan).ConfigureAwait(false);
                changed = true;
            }

            if (!string.Equals(keeper.PeerNetworkIdShort, canonicalId, StringComparison.Ordinal) &&
                canonicalId.Length > 0)
                keeper.PeerNetworkIdShort = canonicalId;
            keeper.UpdatedUtcTicks = Math.Max(keeper.UpdatedUtcTicks,
                members.Max(c => c.UpdatedUtcTicks));
            await conn.UpdateAsync(keeper).ConfigureAwait(false);
            result.Add(keeper);
            changed = true;
        }

        if (changed && notify)
            NotifyChatListChanged();
        return result;
    }

    private static async Task<ChatEntity> PickChatToKeepAsync(SQLiteAsyncConnection conn, List<ChatEntity> members)
    {
        ChatEntity? best = null;
        var bestCount = -1;
        foreach (var chat in members.OrderBy(c => c.Id))
        {
            var count = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM messages WHERE ChatId = ?", chat.Id).ConfigureAwait(false);
            if (best == null || count > bestCount ||
                (count == bestCount && chat.UpdatedUtcTicks > best.UpdatedUtcTicks))
            {
                best = chat;
                bestCount = count;
            }
        }

        return best!;
    }

    private static string MergePeerEndpointsJson(string? a, string? b)
    {
        var merged = new List<TransportAddress>();
        if (!string.IsNullOrWhiteSpace(a))
            merged.AddRange(PeerTransportEndpoints.Parse(new ChatEntity { PeerEndpointsJson = a }));
        if (!string.IsNullOrWhiteSpace(b))
            merged.AddRange(PeerTransportEndpoints.Parse(new ChatEntity { PeerEndpointsJson = b }));
        var dedup = new Dictionary<string, TransportAddress>(StringComparer.Ordinal);
        foreach (var x in merged)
            dedup[$"{(int)x.Kind}:{Convert.ToBase64String(x.Data)}"] = x;
        return PeerTransportEndpoints.Serialize(dedup.Values);
    }

    /// <returns>True if the stored MAC / endpoints actually changed.</returns>
    public async Task<bool> ReplaceChatBluetoothMacAsync(int chatId, string mac)
    {
        if (!BluetoothTransportAddress.TryParseMac(mac, out var macBytes))
            return false;

        var conn = await _db.GetConnectionAsync();
        var chat = await conn.FindAsync<ChatEntity>(chatId);
        if (chat == null)
            return false;

        var newHost = PeerHostList.ReplaceBluetoothMac(chat.PeerHost, mac);
        var btEndpoint = BluetoothTransportAddress.FromMac(macBytes);
        var newEndpointsJson = PeerTransportEndpoints.ReplaceBluetooth(PeerTransportEndpoints.Parse(chat), btEndpoint);

        if (string.Equals(newHost, chat.PeerHost, StringComparison.Ordinal)
            && string.Equals(newEndpointsJson, chat.PeerEndpointsJson, StringComparison.Ordinal))
            return false;

        chat.PeerHost = newHost;
        chat.PeerEndpointsJson = newEndpointsJson;
        chat.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
        await conn.UpdateAsync(chat);
        return true;
    }

    /// <summary>
    ///     Replace all peer endpoints of <paramref name="kind"/> (e.g. Meshtastic = 5) with
    ///     <paramref name="ofKind"/>. Returns true when the stored JSON changed.
    /// </summary>
    public async Task<bool> ReplaceChatTransportEndpointsOfKindAsync(
        int chatId, byte kind, IReadOnlyList<TransportAddress> ofKind)
    {
        var conn = await _db.GetConnectionAsync().ConfigureAwait(false);
        var chat = await conn.FindAsync<ChatEntity>(chatId).ConfigureAwait(false);
        if (chat == null)
            return false;

        var newEndpointsJson = PeerTransportEndpoints.ReplaceKind(PeerTransportEndpoints.Parse(chat), kind, ofKind);
        if (string.Equals(newEndpointsJson, chat.PeerEndpointsJson ?? "", StringComparison.Ordinal))
            return false;

        chat.PeerEndpointsJson = newEndpointsJson;
        chat.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
        await conn.UpdateAsync(chat).ConfigureAwait(false);
        return true;
    }

    public Task UpdateChatDeliveryPathAsync(int chatId, int deliveryPath) =>
        _db.WriteAsync(conn => conn.ExecuteAsync(
            "UPDATE chats SET DeliveryPath = ? WHERE Id = ?", deliveryPath, chatId));

    /// <summary>
    /// Records that GET /bots already classified this peer as not a bot (or probe completed with no match).
    /// </summary>
    public async Task<bool> TryMarkBotCatalogCheckedAsync(int chatId)
    {
        var conn = await _db.GetConnectionAsync().ConfigureAwait(false);
        var chat = await conn.FindAsync<ChatEntity>(chatId).ConfigureAwait(false);
        if (chat == null || chat.BotCatalogChecked)
            return false;

        chat.BotCatalogChecked = true;
        chat.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
        await conn.UpdateAsync(chat).ConfigureAwait(false);
        return true;
    }

    /// <summary>Marks a chat as bot and refreshes its message public key when the catalog key changed.</summary>
    /// <param name="catalogNetworkId">
    /// When set, rewrites <see cref="ChatEntity.PeerNetworkIdShort"/> to the catalog NetworkId
    /// (e.g. chat was stored under BotName).
    /// </param>
    public async Task<bool> TryApplyBotCatalogKeyAsync(
        int chatId,
        string messagePublicKey,
        string? catalogNetworkId = null)
    {
        var pub = messagePublicKey?.Trim() ?? "";
        if (pub.Length == 0)
            return false;

        var conn = await _db.GetConnectionAsync().ConfigureAwait(false);
        var chat = await conn.FindAsync<ChatEntity>(chatId).ConfigureAwait(false);
        if (chat == null)
            return false;

        var canonicalCatalogId = CanonicalPeerNetworkId(catalogNetworkId);
        var idChanged = canonicalCatalogId.Length > 0 &&
                        !PeerNetworkIdsEqual(chat.PeerNetworkIdShort, canonicalCatalogId);
        var pubChanged = !SafetyNumber.PublicKeyJsonEquals(chat.PeerRsaPublicJson, pub);
        var flagChanged = !chat.IsBot;
        var checkedChanged = !chat.BotCatalogChecked;
        if (!pubChanged && !flagChanged && !idChanged && !checkedChanged)
            return false;

        var previous = chat.PeerRsaPublicJson;
        chat.IsBot = true;
        chat.BotCatalogChecked = true;
        chat.PeerRsaPublicJson = pub;
        if (idChanged)
            chat.PeerNetworkIdShort = canonicalCatalogId;
        chat.PeerKeySourceKind = PeerKeySource.Server("bots").Kind;
        chat.PeerKeySourceDetail = PeerKeySource.Server("bots").Detail;
        chat.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
        await conn.UpdateAsync(chat).ConfigureAwait(false);
        if (pubChanged && !string.IsNullOrWhiteSpace(previous))
            RaisePeerPublicKeyChanged(chat, previous, pub);
        return true;
    }

    public async Task UpdateChatP2pRouteAsync(int chatId, string peerHost, int peerPort, string? relayRouteBlob,
        string? peerRsaPublicJson = null, PeerKeySource? keySource = null)
    {
        var conn = await _db.GetConnectionAsync();
        var chat = await conn.FindAsync<ChatEntity>(chatId);
        if (chat == null) return;
        chat.PeerHost = peerHost.Trim();
        chat.PeerPort = peerPort;
        chat.PeerEndpointsJson = MergePeerEndpoints(chat, peerHost, peerPort);
        chat.RelayRouteBlob = relayRouteBlob is null || string.IsNullOrWhiteSpace(relayRouteBlob)
            ? null
            : relayRouteBlob.Trim();
        if (peerRsaPublicJson != null)
        {
            var trimmed = peerRsaPublicJson.Trim();
            if (trimmed.Length > 0)
            {
                var previous = chat.PeerRsaPublicJson;
                var pubChanged = !SafetyNumber.PublicKeyJsonEquals(previous, trimmed);
                chat.PeerRsaPublicJson = trimmed;
                if (keySource.HasValue &&
                    (pubChanged || string.IsNullOrWhiteSpace(chat.PeerKeySourceKind)))
                {
                    chat.PeerKeySourceKind = keySource.Value.Kind;
                    chat.PeerKeySourceDetail = keySource.Value.Detail;
                }

                if (pubChanged && !string.IsNullOrWhiteSpace(previous))
                    RaisePeerPublicKeyChanged(chat, previous, trimmed);
            }
        }
        else if (keySource.HasValue && string.IsNullOrWhiteSpace(chat.PeerKeySourceKind))
        {
            chat.PeerKeySourceKind = keySource.Value.Kind;
            chat.PeerKeySourceDetail = keySource.Value.Detail;
        }

        chat.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
        await conn.UpdateAsync(chat);
    }

    private void RaisePeerPublicKeyChanged(ChatEntity chat, string previousJson, string newJson)
    {
        RaiseEvent(PeerPublicKeyChanged, new PeerPublicKeyChangedEventArgs
        {
            ChatId = chat.Id,
            PeerNickname = chat.PeerNickname,
            PeerNetworkIdShort = chat.PeerNetworkIdShort,
            PreviousSafetyNumber = SafetyNumber.FromPublicKeyJsonOrEmpty(previousJson),
            NewSafetyNumber = SafetyNumber.FromPublicKeyJsonOrEmpty(newJson)
        });
    }

    /// <summary>
    /// Обновляет ник пира, если пришёл реальный ник (не пустой и не совпадающий с network id),
    /// а в БД сейчас пусто / network id / другой устаревший placeholder.
    /// </summary>
    public async Task<bool> TryUpdatePeerNicknameAsync(int chatId, string? peerNickname)
    {
        var conn = await _db.GetConnectionAsync().ConfigureAwait(false);
        var chat = await conn.FindAsync<ChatEntity>(chatId).ConfigureAwait(false);
        if (chat == null)
            return false;

        var preferred = PreferPeerNickname(peerNickname, chat.PeerNetworkIdShort);
        if (!ShouldReplacePeerNickname(chat.PeerNickname, preferred, chat.PeerNetworkIdShort))
            return false;

        chat.PeerNickname = preferred;
        chat.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
        await conn.UpdateAsync(chat).ConfigureAwait(false);
        await NotifyChatListChangedUnlessBlockedAsync(chat.UserId, chat.PeerNetworkIdShort).ConfigureAwait(false);
        return true;
    }

    public static bool IsPlaceholderNickname(string? nickname, string networkIdShort)
    {
        var nick = nickname?.Trim() ?? "";
        var id = networkIdShort?.Trim() ?? "";
        return nick.Length == 0 ||
               (id.Length > 0 && string.Equals(nick, id, StringComparison.OrdinalIgnoreCase));
    }

    private static string PreferPeerNickname(string? peerNickname, string networkIdShort)
    {
        var nick = peerNickname?.Trim() ?? "";
        var id = networkIdShort.Trim();
        
        return nick.Length == 0 ? id : nick;
    }

    private static bool ShouldReplacePeerNickname(string? currentNickname, string incomingNickname, string networkIdShort)
    {
        var incoming = PreferPeerNickname(incomingNickname, networkIdShort);
        var current = currentNickname?.Trim() ?? "";
        if (string.Equals(current, incoming, StringComparison.Ordinal))
            return false;

        var incomingIsPlaceholder = IsPlaceholderNickname(incoming, networkIdShort);
        var currentIsPlaceholder = IsPlaceholderNickname(current, networkIdShort);
        if (incomingIsPlaceholder)
            return current.Length == 0;
        return currentIsPlaceholder || !string.Equals(current, incoming, StringComparison.Ordinal);
    }

    private static string MergePeerEndpoints(ChatEntity? existing, string peerHost, int peerPort)
    {
        var merged = new List<TransportAddress>();
        if (existing != null)
            merged.AddRange(PeerTransportEndpoints.Parse(existing));
        foreach (var host in (peerHost ?? string.Empty).Split([',', ';', '|', ' ', '\n', '\r', '\t'],
#if NET5_0_OR_GREATER
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
#else
                     StringSplitOptions.RemoveEmptyEntries))
#endif
            if (IPAddress.TryParse(host, out var ip) && peerPort is >= 1 and <= 65535)
                merged.Add(UdpTransportAddress.FromIPEndPoint(new IPEndPoint(ip, peerPort)));
            else if (BluetoothTransportAddress.TryParseMac(host, out var mac))
                merged.Add(BluetoothTransportAddress.FromMac(mac));

        var dedup = new Dictionary<string, TransportAddress>(StringComparer.Ordinal);
        foreach (var x in merged)
            dedup[$"{(int)x.Kind}:{Convert.ToBase64String(x.Data)}"] = x;
        return PeerTransportEndpoints.Serialize(dedup.Values);
    }

    public async Task<IReadOnlyList<ChatMessageEntity>> ListMessagesAsync(int chatId)
    {
        var conn = await _db.GetReadConnectionAsync().ConfigureAwait(false);
        return await conn.Table<ChatMessageEntity>()
            .Where(m => m.ChatId == chatId)
            .OrderBy(m => m.SentUtcTicks)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ChatMessageEntity>> ListMessagesPageDescAsync(int chatId, int offset, int limit) =>
        ListMessagesPageDescAsync(chatId, offset, limit, includePayloadBlob: true);

    /// <param name="includePayloadBlob">
    ///     False skips <see cref="ChatMessageEntity.ImageBlob"/> so chat lists/history stay off the UI thread.
    /// </param>
    public async Task<IReadOnlyList<ChatMessageEntity>> ListMessagesPageDescAsync(
        int chatId, int offset, int limit, bool includePayloadBlob)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var conn = await _db.GetReadConnectionAsync().ConfigureAwait(false);
        if (includePayloadBlob)
        {
            var withBlob = await conn.Table<ChatMessageEntity>()
                .Where(m => m.ChatId == chatId)
                .OrderByDescending(m => m.SentUtcTicks)
                .ThenByDescending(m => m.Id)
                .Skip(offset)
                .Take(limit)
                .ToListAsync()
                .ConfigureAwait(false);
            foreach (var m in withBlob)
                m.HasPayloadBlob = m.ImageBlob is { Length: > 0 };
            return withBlob;
        }

        // length(ImageBlob) stays in SQLite; only 0/1 is returned — keeps history/list fast when chats have media.
        var rows = await conn.QueryAsync<MessageListSqlRow>(
                "SELECT Id, ChatId, Outgoing, Text, SentUtcTicks, DeliveryStatus, PayloadKind, MimeType, " +
                "TransferId, TransferToken, TransferPayloadKind, TransferFileName, " +
                "TransferSizeBytes, TransferHost, TransferPort, TransferExpiresUtcTicks, TransferState, " +
                "(CASE WHEN ImageBlob IS NOT NULL AND length(ImageBlob) > 0 THEN 1 ELSE 0 END) AS HasPayloadBlob " +
                "FROM messages WHERE ChatId = ? ORDER BY SentUtcTicks DESC, Id DESC LIMIT ? OFFSET ?",
                chatId, limit, offset)
            .ConfigureAwait(false);
        return rows.Select(static r => new ChatMessageEntity
        {
            Id = r.Id,
            ChatId = r.ChatId,
            Outgoing = r.Outgoing,
            Text = r.Text ?? "",
            SentUtcTicks = r.SentUtcTicks,
            DeliveryStatus = r.DeliveryStatus,
            PayloadKind = r.PayloadKind,
            MimeType = r.MimeType ?? "",
            ImageBlob = null,
            HasPayloadBlob = r.HasPayloadBlob != 0,
            TransferId = r.TransferId ?? "",
            TransferToken = r.TransferToken ?? "",
            TransferPayloadKind = r.TransferPayloadKind ?? "",
            TransferFileName = r.TransferFileName ?? "",
            TransferSizeBytes = r.TransferSizeBytes,
            TransferHost = r.TransferHost ?? "",
            TransferPort = r.TransferPort,
            TransferExpiresUtcTicks = r.TransferExpiresUtcTicks,
            TransferState = r.TransferState
        }).ToList();
    }

    private sealed class MessageListSqlRow
    {
        public int Id { get; set; }
        public int ChatId { get; set; }
        public bool Outgoing { get; set; }
        public string? Text { get; set; }
        public long SentUtcTicks { get; set; }
        public int DeliveryStatus { get; set; }
        public int PayloadKind { get; set; }
        public string? MimeType { get; set; }
        public string? TransferId { get; set; }
        public string? TransferToken { get; set; }
        public string? TransferPayloadKind { get; set; }
        public string? TransferFileName { get; set; }
        public long TransferSizeBytes { get; set; }
        public string? TransferHost { get; set; }
        public int TransferPort { get; set; }
        public long TransferExpiresUtcTicks { get; set; }
        public int TransferState { get; set; }
        public int HasPayloadBlob { get; set; }
    }

    /// <summary>
    /// Claims a server MessageId for local ingest. Returns true if this is the first time we see it
    /// (caller should insert the message). Returns false if already claimed (duplicate from another server).
    /// </summary>
    public async Task<bool> TryClaimServerMessageAsync(string? messageId)
    {
        var id = messageId?.Trim() ?? "";
        if (id.Length == 0)
            return true;

        var conn = await _db.GetConnectionAsync().ConfigureAwait(false);
        var existing = await conn.FindAsync<SeenServerMessageEntity>(id).ConfigureAwait(false);
        if (existing != null)
            return false;

        try
        {
            await conn.InsertAsync(new SeenServerMessageEntity
            {
                MessageId = id,
                SeenUtcTicks = DateTime.UtcNow.Ticks
            }).ConfigureAwait(false);
        }
        catch (SQLiteException)
        {
            // Concurrent insert of the same primary key.
            return false;
        }

        // Keep the table bounded (MessageIds are GUIDs; inbox fan-out only needs recent history).
        if ((Interlocked.Increment(ref _seenPruneCounter) & 31) == 0)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-30).Ticks;
                await conn.ExecuteAsync(
                        "DELETE FROM seen_server_messages WHERE SeenUtcTicks < ?", cutoff)
                    .ConfigureAwait(false);
            }
            catch
            {
                // ignore prune failures
            }
        }

        return true;
    }

    /// <summary>
    /// Remembers that local outgoing <paramref name="localMessageId"/> was posted to messenger servers
    /// under <paramref name="serverMessageId"/> (for delivery-receipt correlation).
    /// </summary>
    public async Task RegisterOutgoingServerMessageAsync(string serverMessageId, int localMessageId, int chatId)
    {
        var id = serverMessageId?.Trim() ?? "";
        if (id.Length == 0 || localMessageId <= 0 || chatId <= 0)
            return;

        await _db.WriteAsync(async conn =>
        {
            var existing = await conn.FindAsync<OutgoingServerMessageEntity>(id).ConfigureAwait(false);
            if (existing != null)
            {
                existing.LocalMessageId = localMessageId;
                existing.ChatId = chatId;
                existing.CreatedUtcTicks = DateTime.UtcNow.Ticks;
                await conn.UpdateAsync(existing).ConfigureAwait(false);
                return;
            }

            try
            {
                await conn.InsertAsync(new OutgoingServerMessageEntity
                {
                    ServerMessageId = id,
                    LocalMessageId = localMessageId,
                    ChatId = chatId,
                    CreatedUtcTicks = DateTime.UtcNow.Ticks
                }).ConfigureAwait(false);
            }
            catch (SQLiteException)
            {
                // Concurrent insert of the same primary key — treat as already registered.
            }
        }).ConfigureAwait(false);

        if ((Interlocked.Increment(ref _seenPruneCounter) & 31) == 0)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-30).Ticks;
                await _db.WriteAsync(conn => conn.ExecuteAsync(
                        "DELETE FROM outgoing_server_messages WHERE CreatedUtcTicks < ?", cutoff))
                    .ConfigureAwait(false);
            }
            catch
            {
                // ignore prune failures
            }
        }
    }

    /// <summary>
    /// Looks up and removes a pending outgoing server-message mapping.
    /// Returns local message id + chat id when found.
    /// </summary>
    public async Task<(int LocalMessageId, int ChatId)?> TryTakeOutgoingServerMessageAsync(string? serverMessageId)
    {
        var id = serverMessageId?.Trim() ?? "";
        if (id.Length == 0)
            return null;

        return await _db.WriteAsync(async conn =>
        {
            var row = await conn.FindAsync<OutgoingServerMessageEntity>(id).ConfigureAwait(false);
            if (row == null)
                return ((int LocalMessageId, int ChatId)?)null;

            await conn.DeleteAsync(row).ConfigureAwait(false);
            return (row.LocalMessageId, row.ChatId);
        }).ConfigureAwait(false);
    }

    public async Task<int> AddMessageAsync(int chatId, bool outgoing, string text,
        MessageDeliveryStatus deliveryStatus = MessageDeliveryStatus.Delivered)
    {
        return await _db.WriteAsync(async conn =>
        {
            var status = outgoing
                ? deliveryStatus
                : MessageDeliveryStatus.NotApplicable;
            var msg = new ChatMessageEntity
            {
                ChatId = chatId,
                Outgoing = outgoing,
                Text = text,
                SentUtcTicks = DateTime.UtcNow.Ticks,
                DeliveryStatus = (int)status,
                PayloadKind = (int)ChatPayloadKind.Text,
                MimeType = "",
                ImageBlob = null,
                TransferId = "",
                TransferToken = "",
                TransferPayloadKind = "",
                TransferFileName = "",
                TransferSizeBytes = 0,
                TransferHost = "",
                TransferPort = 0,
                TransferExpiresUtcTicks = 0,
                TransferState = (int)ChatTransferState.None
            };
            await conn.InsertAsync(msg).ConfigureAwait(false);

            var chat = await conn.FindAsync<ChatEntity>(chatId).ConfigureAwait(false);
            if (chat != null)
            {
                chat.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                await conn.UpdateAsync(chat).ConfigureAwait(false);
            }

            await RaiseChatMessageAppendedAsync(chatId, outgoing, chat).ConfigureAwait(false);
            return msg.Id;
        }).ConfigureAwait(false);
    }

    public async Task<int> AddImageMessageAsync(int chatId, bool outgoing, string mimeType, byte[] imageBytes,
        MessageDeliveryStatus deliveryStatus = MessageDeliveryStatus.Delivered)
    {
        Require.NotNull(imageBytes);
        return await _db.WriteAsync(async conn =>
        {
            var status = outgoing
                ? deliveryStatus
                : MessageDeliveryStatus.NotApplicable;
            var msg = new ChatMessageEntity
            {
                ChatId = chatId,
                Outgoing = outgoing,
                Text = "",
                SentUtcTicks = DateTime.UtcNow.Ticks,
                DeliveryStatus = (int)status,
                PayloadKind = (int)ChatPayloadKind.Image,
                MimeType = mimeType.Trim(),
                ImageBlob = imageBytes,
                TransferId = "",
                TransferToken = "",
                TransferPayloadKind = "",
                TransferFileName = "",
                TransferSizeBytes = 0,
                TransferHost = "",
                TransferPort = 0,
                TransferExpiresUtcTicks = 0,
                TransferState = (int)ChatTransferState.None
            };
            await conn.InsertAsync(msg).ConfigureAwait(false);

            var chat = await conn.FindAsync<ChatEntity>(chatId).ConfigureAwait(false);
            if (chat != null)
            {
                chat.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                await conn.UpdateAsync(chat).ConfigureAwait(false);
            }

            await RaiseChatMessageAppendedAsync(chatId, outgoing, chat).ConfigureAwait(false);
            return msg.Id;
        }).ConfigureAwait(false);
    }

    public async Task<int> AddFileMessageAsync(int chatId, bool outgoing, string fileName, string mimeType,
        byte[] fileBytes, MessageDeliveryStatus deliveryStatus = MessageDeliveryStatus.Delivered)
    {
        Require.NotNull(fileBytes);
        return await _db.WriteAsync(async conn =>
        {
            var status = outgoing
                ? deliveryStatus
                : MessageDeliveryStatus.NotApplicable;
            var msg = new ChatMessageEntity
            {
                ChatId = chatId,
                Outgoing = outgoing,
                Text = fileName.Trim(),
                SentUtcTicks = DateTime.UtcNow.Ticks,
                DeliveryStatus = (int)status,
                PayloadKind = (int)ChatPayloadKind.File,
                MimeType = mimeType.Trim(),
                ImageBlob = fileBytes,
                TransferId = "",
                TransferToken = "",
                TransferPayloadKind = "",
                TransferFileName = "",
                TransferSizeBytes = 0,
                TransferHost = "",
                TransferPort = 0,
                TransferExpiresUtcTicks = 0,
                TransferState = (int)ChatTransferState.None
            };
            await conn.InsertAsync(msg).ConfigureAwait(false);

            var chat = await conn.FindAsync<ChatEntity>(chatId).ConfigureAwait(false);
            if (chat != null)
            {
                chat.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                await conn.UpdateAsync(chat).ConfigureAwait(false);
            }

            await RaiseChatMessageAppendedAsync(chatId, outgoing, chat).ConfigureAwait(false);
            return msg.Id;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts an incoming transfer-offer row in one write so UI never sees a bare text stub
    /// ("image" / "video") before payload/transfer metadata is applied.
    /// </summary>
    public async Task<int> AddIncomingTransferOfferAsync(
        int chatId,
        string displayText,
        string mimeType,
        string transferId,
        string transferToken,
        string transferPayloadKind,
        string transferFileName,
        long transferSizeBytes,
        string transferHost,
        int transferPort,
        long transferExpiresUtcTicks,
        int transferChunkCount = 0)
    {
        var text = string.IsNullOrWhiteSpace(displayText)
            ? (string.IsNullOrWhiteSpace(transferFileName) ? "[Входящее вложение]" : transferFileName.Trim())
            : displayText.Trim();
        return await _db.WriteAsync(async conn =>
        {
            var msg = new ChatMessageEntity
            {
                ChatId = chatId,
                Outgoing = false,
                Text = text,
                SentUtcTicks = DateTime.UtcNow.Ticks,
                DeliveryStatus = (int)MessageDeliveryStatus.NotApplicable,
                PayloadKind = (int)ChatPayloadKind.TransferOffer,
                MimeType = mimeType?.Trim() ?? "",
                ImageBlob = null,
                TransferId = transferId?.Trim() ?? "",
                TransferToken = transferToken?.Trim() ?? "",
                TransferPayloadKind = transferPayloadKind?.Trim() ?? "",
                TransferFileName = transferFileName?.Trim() ?? "",
                TransferSizeBytes = Math.Max(0, transferSizeBytes),
                TransferHost = transferHost?.Trim() ?? "",
                TransferPort = transferPort,
                TransferExpiresUtcTicks = transferExpiresUtcTicks,
                TransferState = (int)ChatTransferState.AwaitingClick,
                TransferChunkCount = Math.Max(0, transferChunkCount)
            };
            await conn.InsertAsync(msg).ConfigureAwait(false);

            var chat = await conn.FindAsync<ChatEntity>(chatId).ConfigureAwait(false);
            if (chat != null)
            {
                chat.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                await conn.UpdateAsync(chat).ConfigureAwait(false);
            }

            await RaiseChatMessageAppendedAsync(chatId, outgoing: false, chat).ConfigureAwait(false);
            return msg.Id;
        }).ConfigureAwait(false);
    }

    public async Task<ChatMessageEntity?> GetMessageAsync(int messageId) =>
        await GetMessageAsync(messageId, includePayloadBlob: true).ConfigureAwait(false);

    /// <param name="includePayloadBlob">
    ///     False skips <see cref="ChatMessageEntity.ImageBlob"/> so outbound flush / status work
    ///     does not pull multi‑MB attachments into memory or lock SQLite on every tick.
    /// </param>
    public async Task<ChatMessageEntity?> GetMessageAsync(int messageId, bool includePayloadBlob)
    {
        var conn = await _db.GetReadConnectionAsync().ConfigureAwait(false);
        if (includePayloadBlob)
            return await conn.FindAsync<ChatMessageEntity>(messageId).ConfigureAwait(false);

        var rows = await conn.QueryAsync<MessageListSqlRow>(
                "SELECT Id, ChatId, Outgoing, Text, SentUtcTicks, DeliveryStatus, PayloadKind, MimeType, " +
                "TransferId, TransferToken, TransferPayloadKind, TransferFileName, " +
                "TransferSizeBytes, TransferHost, TransferPort, TransferExpiresUtcTicks, TransferState, " +
                "(CASE WHEN ImageBlob IS NOT NULL AND length(ImageBlob) > 0 THEN 1 ELSE 0 END) AS HasPayloadBlob " +
                "FROM messages WHERE Id = ? LIMIT 1",
                messageId)
            .ConfigureAwait(false);
        var r = rows.FirstOrDefault();
        if (r == null)
            return null;
        return new ChatMessageEntity
        {
            Id = r.Id,
            ChatId = r.ChatId,
            Outgoing = r.Outgoing,
            Text = r.Text ?? "",
            SentUtcTicks = r.SentUtcTicks,
            DeliveryStatus = r.DeliveryStatus,
            PayloadKind = r.PayloadKind,
            MimeType = r.MimeType ?? "",
            ImageBlob = null,
            HasPayloadBlob = r.HasPayloadBlob != 0,
            TransferId = r.TransferId ?? "",
            TransferToken = r.TransferToken ?? "",
            TransferPayloadKind = r.TransferPayloadKind ?? "",
            TransferFileName = r.TransferFileName ?? "",
            TransferSizeBytes = r.TransferSizeBytes,
            TransferHost = r.TransferHost ?? "",
            TransferPort = r.TransferPort,
            TransferExpiresUtcTicks = r.TransferExpiresUtcTicks,
            TransferState = r.TransferState
        };
    }

    public async Task UpdateMessageDeliveryStatusAsync(int messageId, MessageDeliveryStatus status)
    {
        ChatMessageEntity? row = null;
        var changed = false;
        await _db.WriteAsync(async conn =>
        {
            row = await conn.FindAsync<ChatMessageEntity>(messageId).ConfigureAwait(false);
            if (row == null)
                return;

            // Never downgrade Delivered → Sent/Pending (receipt may race with local Sent update).
            var current = (MessageDeliveryStatus)row.DeliveryStatus;
            if (current == MessageDeliveryStatus.Delivered &&
                status is MessageDeliveryStatus.Sent or MessageDeliveryStatus.Pending)
                return;

            if (current == status)
                return;

            await conn.ExecuteAsync(
                "UPDATE messages SET DeliveryStatus = ? WHERE Id = ?",
                (int)status, messageId).ConfigureAwait(false);
            row.DeliveryStatus = (int)status;
            changed = true;
        }).ConfigureAwait(false);

        if (changed && row is { Outgoing: true })
            RaiseEvent(ChatMessageDeliveryChanged, new ChatMessageAppendedEventArgs(row.ChatId, outgoing: true));
    }

    public Task UpdateTransferStateAsync(int messageId, ChatTransferState state) =>
        _db.WriteAsync(conn => conn.ExecuteAsync(
            "UPDATE messages SET TransferState = ? WHERE Id = ?",
            (int)state, messageId));

    public Task UpdateMessageTransferMetadataAsync(int messageId, string transferId, string transferToken,
        string transferPayloadKind, string transferFileName, long transferSizeBytes, string transferHost,
        int transferPort,
        long transferExpiresUtcTicks, ChatTransferState transferState) =>
        _db.WriteAsync(conn => conn.ExecuteAsync(
            "UPDATE messages SET TransferId = ?, TransferToken = ?, TransferPayloadKind = ?, " +
            "TransferFileName = ?, TransferSizeBytes = ?, TransferHost = ?, TransferPort = ?, " +
            "TransferExpiresUtcTicks = ?, TransferState = ? WHERE Id = ?",
            transferId?.Trim() ?? "",
            transferToken?.Trim() ?? "",
            transferPayloadKind?.Trim() ?? "",
            transferFileName?.Trim() ?? "",
            Math.Max(0, transferSizeBytes),
            transferHost?.Trim() ?? "",
            transferPort,
            transferExpiresUtcTicks,
            (int)transferState,
            messageId));

    public async Task UpdateMessagePayloadAsync(int messageId, ChatPayloadKind payloadKind, string text,
        string mimeType,
        byte[] payloadBytes)
    {
        var conn = await _db.GetConnectionAsync().ConfigureAwait(false);
        // When promoting File → TransferOffer the blob is already on the row; rewriting it again
        // doubles SQLite write time and blocks inbox ingest / text send.
        if (payloadBytes is { Length: > 0 })
        {
            var existingLen = await conn.ExecuteScalarAsync<long>(
                    "SELECT COALESCE(length(ImageBlob), 0) FROM messages WHERE Id = ?", messageId)
                .ConfigureAwait(false);
            if (existingLen == payloadBytes.Length)
            {
                await conn.ExecuteAsync(
                        "UPDATE messages SET PayloadKind = ?, Text = ?, MimeType = ? WHERE Id = ?",
                        (int)payloadKind, text ?? "", mimeType ?? "", messageId)
                    .ConfigureAwait(false);
                return;
            }
        }

        var m = await conn.FindAsync<ChatMessageEntity>(messageId).ConfigureAwait(false);
        if (m == null)
            return;
        m.PayloadKind = (int)payloadKind;
        m.Text = text ?? "";
        m.MimeType = mimeType ?? "";
        m.ImageBlob = payloadBytes;
        await conn.UpdateAsync(m).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks a stored file row as a transfer-offer without rewriting <see cref="ChatMessageEntity.ImageBlob"/>.
    /// </summary>
    public async Task PromoteFileToTransferOfferAsync(
        int messageId,
        string transferId,
        string transferToken,
        string transferPayloadKind,
        string fileName,
        string mimeType,
        long sizeBytes,
        long expiresUtcTicks)
    {
        var conn = await _db.GetConnectionAsync().ConfigureAwait(false);
        await conn.ExecuteAsync(
                "UPDATE messages SET PayloadKind = ?, Text = ?, MimeType = ?, " +
                "TransferId = ?, TransferToken = ?, TransferPayloadKind = ?, TransferFileName = ?, " +
                "TransferSizeBytes = ?, TransferHost = ?, TransferPort = ?, TransferExpiresUtcTicks = ?, " +
                "TransferState = ? WHERE Id = ?",
                (int)ChatPayloadKind.TransferOffer,
                fileName ?? "",
                mimeType ?? "",
                transferId?.Trim() ?? "",
                transferToken?.Trim() ?? "",
                transferPayloadKind?.Trim() ?? "",
                fileName?.Trim() ?? "",
                Math.Max(0, sizeBytes),
                "",
                0,
                expiresUtcTicks,
                (int)ChatTransferState.Offered,
                messageId)
            .ConfigureAwait(false);
    }

    public async Task UpdateBotChunkLocatorAsync(int messageId, string host, int chunkCount, string sha256)
    {
        var conn = await _db.GetConnectionAsync().ConfigureAwait(false);
        await conn.ExecuteAsync(
                "UPDATE messages SET TransferHost = ?, TransferChunkCount = ?, TransferToken = ? WHERE Id = ?",
                host?.Trim() ?? "",
                Math.Max(0, chunkCount),
                sha256?.Trim() ?? "",
                messageId)
            .ConfigureAwait(false);
    }

    public async Task<ChatMessageEntity?> FindOutgoingTransferByIdAsync(int chatId, string transferId)
    {
        var id = transferId?.Trim() ?? "";
        if (id.Length == 0)
            return null;
        var conn = await _db.GetReadConnectionAsync().ConfigureAwait(false);
        var rows = await conn.QueryAsync<MessageListSqlRow>(
                "SELECT Id, ChatId, Outgoing, Text, SentUtcTicks, DeliveryStatus, PayloadKind, MimeType, " +
                "TransferId, TransferToken, TransferPayloadKind, TransferFileName, " +
                "TransferSizeBytes, TransferHost, TransferPort, TransferExpiresUtcTicks, TransferState, " +
                "(CASE WHEN ImageBlob IS NOT NULL AND length(ImageBlob) > 0 THEN 1 ELSE 0 END) AS HasPayloadBlob " +
                "FROM messages WHERE ChatId = ? AND Outgoing = 1 AND TransferId = ? " +
                "ORDER BY Id DESC LIMIT 1",
                chatId, id)
            .ConfigureAwait(false);
        var r = rows.FirstOrDefault();
        if (r == null)
            return null;
        return new ChatMessageEntity
        {
            Id = r.Id,
            ChatId = r.ChatId,
            Outgoing = r.Outgoing,
            Text = r.Text ?? "",
            SentUtcTicks = r.SentUtcTicks,
            DeliveryStatus = r.DeliveryStatus,
            PayloadKind = r.PayloadKind,
            MimeType = r.MimeType ?? "",
            ImageBlob = null,
            HasPayloadBlob = r.HasPayloadBlob != 0,
            TransferId = r.TransferId ?? "",
            TransferToken = r.TransferToken ?? "",
            TransferPayloadKind = r.TransferPayloadKind ?? "",
            TransferFileName = r.TransferFileName ?? "",
            TransferSizeBytes = r.TransferSizeBytes,
            TransferHost = r.TransferHost ?? "",
            TransferPort = r.TransferPort,
            TransferExpiresUtcTicks = r.TransferExpiresUtcTicks,
            TransferState = r.TransferState
        };
    }

    /// <summary>
    ///     Удаляет все сообщения чата локально. Чат остаётся. Возвращает false, если чат не найден или не принадлежит
    ///     userId.
    /// </summary>
    public async Task<bool> ClearMessagesAsync(int chatId, int userId, CancellationToken cancellationToken = default)
    {
        var conn = await _db.GetConnectionAsync().ConfigureAwait(false);
        var chat = await conn.FindAsync<ChatEntity>(chatId).ConfigureAwait(false);
        if (chat == null || chat.UserId != userId)
            return false;

        await conn.ExecuteAsync("DELETE FROM messages WHERE ChatId = ?", chatId).ConfigureAwait(false);
        chat.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
        await conn.UpdateAsync(chat).ConfigureAwait(false);
        return true;
    }

    /// <summary>Удаляет чат и все его сообщения локально. Возвращает false, если чат не найден или не принадлежит userId.</summary>
    public async Task<bool> DeleteChatAsync(int chatId, int userId, CancellationToken cancellationToken = default)
    {
        var conn = await _db.GetConnectionAsync().ConfigureAwait(false);
        var chat = await conn.FindAsync<ChatEntity>(chatId).ConfigureAwait(false);
        if (chat == null || chat.UserId != userId)
            return false;

        await conn.ExecuteAsync("DELETE FROM messages WHERE ChatId = ?", chatId).ConfigureAwait(false);
        await conn.DeleteAsync(chat).ConfigureAwait(false);
        NotifyChatListChanged();
        return true;
    }
}