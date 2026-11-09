using System.Threading;
using Microsoft.Extensions.Logging;
using ShortP2P.Auth.Data;
using ShortP2P.Client.ChatMedia;
using ShortP2P.Client.Data;
using ShortP2P.Client.Services.MessengerServers;
using ShortP2P.Crypto;

namespace ShortP2P.Client.Services;

/// <summary>
/// Fx48 old windows session: no UDP/BLE P2P.
/// Outbound delivery is a single per-chat background flush worker that reads Pending rows from
/// SQLite and posts via messenger server (<see cref="MessengerServerSyncService.TryDeliverWireAsync"/>
/// or <see cref="MessengerServerSyncService.TryDeliverBotWireAsync"/> for bot chats). Updates
/// Sent / Failed in the DB. Incoming wires stay on the repository path
/// (<see cref="IsReadyForServerReceive"/> is always false).
/// </summary>
public sealed class ChatP2PSession
{
    public const int MaxMessageChars = 32768;

    private readonly object _pendingSync = new();
    private readonly List<int> _pendingOutgoing = [];
    private readonly SemaphoreSlim _flushPendingSem = new(1, 1);
    private readonly ChatRepository _repo;
    private readonly MessengerServerSyncService _servers;
    private readonly UserEntity _user;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _outboundCts = new();

    private ChatEntity _chat;
    private int _flushWorkerRunning;

    public ChatP2PSession(
        ChatEntity chat,
        UserEntity user,
        ChatRepository repo,
        MessengerServerSyncService servers,
        SynchronizationContext? uiSynchronizationContext = null,
        ILogger? logger = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _servers = servers ?? throw new ArgumentNullException(nameof(servers));
        _uiSynchronizationContext = uiSynchronizationContext;
        _logger = logger;
    }

    /// <summary>
    /// Keep false so <c>PersistIncomingServerWireAsync</c> always writes via the repository
    /// (this stub must not swallow inbox wires).
    /// </summary>
    public bool IsReadyForServerReceive => false;

    /// <summary>Fx48 has no mesh P2P path; server inbox stays on the repository.</summary>
    public bool AcceptsServerTransport => true;

    /// <summary>Fx48 is server-only; stored mesh preference is ignored at send time.</summary>
    public ChatDeliveryPath DeliveryPath =>
        Enum.IsDefined(typeof(ChatDeliveryPath), _chat.DeliveryPath)
            ? (ChatDeliveryPath)_chat.DeliveryPath
            : ChatDeliveryPath.Server;

    /// <summary>Fx48 has no Meshtastic/LoRa path.</summary>
    public bool IsMeshtasticPathActive => false;

    /// <summary>Fx48 always delivers via messenger servers.</summary>
    public bool IsCommunicatingViaMessengerServer => true;

    /// <summary>Fx48 does not track a live server pin URL on the session.</summary>
    public string? ActiveMessengerServerBaseUrl =>
        PeerKeySource.IsServer(_chat.PeerKeySourceKind) ? _chat.PeerKeySourceDetail : null;

    public event EventHandler? MessagesChanged;

    public event EventHandler<int>? TransferStateChanged;

    public event EventHandler? DeliveryPathChanged;

    public event EventHandler? ActiveChannelChanged;

    public static string DeliveryPathLabel(ChatDeliveryPath path) => path switch
    {
        ChatDeliveryPath.Server => "Путь: сервер",
        ChatDeliveryPath.Mesh => "Путь: mesh",
        _ => "Путь: авто"
    };

    /// <summary>
    /// Fx48 cannot open UDP/BLE mesh. Server path is a no-op success; mesh throws.
    /// </summary>
    public async Task SwitchDeliveryPathAsync(ChatDeliveryPath path, CancellationToken cancellationToken = default)
    {
        if (path is not (ChatDeliveryPath.Server or ChatDeliveryPath.Mesh))
            throw new ArgumentOutOfRangeException(nameof(path));

        if (path == ChatDeliveryPath.Mesh)
            throw new InvalidOperationException("Mesh (UDP/BLE) недоступен в этой сборке Windows.");

        _chat.DeliveryPath = (int)ChatDeliveryPath.Server;
        await _repo.UpdateChatDeliveryPathAsync(_chat.Id, _chat.DeliveryPath).ConfigureAwait(false);
        DeliveryPathChanged?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads an incoming transfer blob from messenger servers (no TCP P2P on Fx48).
    /// Mirrors the server path of the full <c>ChatP2PSession.RequestBinaryDownloadAsync</c>.
    /// </summary>
    public async Task RequestBinaryDownloadAsync(int messageId, CancellationToken cancellationToken = default)
    {
        var row = await _repo.GetMessageAsync(messageId).ConfigureAwait(false);
        if (row == null || row.ChatId != _chat.Id || row.Outgoing || string.IsNullOrWhiteSpace(row.TransferId))
            return;

        var state = (ChatTransferState)row.TransferState;
        if (state == ChatTransferState.Received && row.ImageBlob is { Length: > 0 })
        {
            if (_chat.IsBot && row.TransferChunkCount > 0)
                await ReceiptBotBlobAsync(row, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (state == ChatTransferState.Transferring)
            return;

        await _repo.UpdateTransferStateAsync(messageId, ChatTransferState.Transferring).ConfigureAwait(false);
        TransferStateChanged?.Invoke(this, messageId);
        RaiseMessagesChanged();

        try
        {
            if (!await TryReceiveBlobFromMessengerServersAsync(row, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Не удалось скачать файл с сервера сообщений. Проверьте подключение и повторите.");
            }
        }
        catch
        {
            await _repo.UpdateTransferStateAsync(messageId, ChatTransferState.Failed).ConfigureAwait(false);
            TransferStateChanged?.Invoke(this, messageId);
            RaiseMessagesChanged();
            throw;
        }
    }

    private async Task<bool> TryReceiveBlobFromMessengerServersAsync(
        ChatMessageEntity row,
        CancellationToken cancellationToken)
    {
        var blobId = row.TransferId.Trim();
        if (blobId.Length == 0)
            return false;

        var hint = LooksLikeHttpBaseUrl(row.TransferHost) ? row.TransferHost : null;
        var chunked = _chat.IsBot && row.TransferChunkCount > 0;
        byte[]? ciphertext;
        if (chunked)
        {
            ciphertext = await _servers.TryDownloadBotChunksAsync(
                    blobId,
                    hint,
                    row.TransferChunkCount,
                    CiphertextShaOrNull(row.TransferToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            ciphertext = await _servers.TryDownloadBlobAsync(
                    blobId, hint, cancellationToken, botServersOnly: _chat.IsBot)
                .ConfigureAwait(false);
            if ((ciphertext == null || ciphertext.Length == 0) && _chat.IsBot)
            {
                ciphertext = await _servers.TryDownloadBotChunksAsync(
                        blobId,
                        hint,
                        0,
                        CiphertextShaOrNull(row.TransferToken),
                        cancellationToken)
                    .ConfigureAwait(false);
                chunked = ciphertext is { Length: > 0 };
            }
        }

        if (ciphertext == null || ciphertext.Length == 0)
            return false;

        if (string.IsNullOrWhiteSpace(_user.RsaPrivateJson))
            return false;

        byte[] wire;
        try
        {
            var privateKey = RsaKeySerializer.DeserializePrivate(_user.RsaPrivateJson);
            wire = MessengerServerPayloadCodec.Decrypt(ciphertext, privateKey);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Chat {ChatId}: blob decrypt failed for message {MessageId}", _chat.Id, row.Id);
            return false;
        }

        if (!ChatWireCodec.TryParse(wire, out var parsed) || parsed == null)
            return false;

        byte[] bytes;
        string mimeType;
        string fileName;
        ChatPayloadKind kind;
        switch (parsed)
        {
            case ChatWireImage img:
                bytes = img.ImageBytes;
                mimeType = img.MimeType;
                fileName = string.IsNullOrWhiteSpace(row.TransferFileName) ? row.Text : row.TransferFileName;
                kind = ChatPayloadKind.Image;
                break;
            case ChatWireFile f:
                bytes = f.FileBytes;
                mimeType = f.MimeType;
                fileName = string.IsNullOrWhiteSpace(f.FileName)
                    ? (string.IsNullOrWhiteSpace(row.TransferFileName) ? row.Text : row.TransferFileName)
                    : f.FileName;
                kind = ChatPayloadKind.File;
                break;
            default:
                return false;
        }

        await _repo.UpdateMessagePayloadAsync(row.Id, kind, fileName, mimeType, bytes).ConfigureAwait(false);
        await _repo.UpdateMessageTransferMetadataAsync(
                row.Id, row.TransferId, row.TransferToken, row.TransferPayloadKind, row.TransferFileName,
                bytes.Length, "", 0, 0, ChatTransferState.Received)
            .ConfigureAwait(false);
        TransferStateChanged?.Invoke(this, row.Id);
        RaiseMessagesChanged();

        try
        {
            if (chunked)
                await ReceiptBotBlobAsync(row, cancellationToken).ConfigureAwait(false);
            else
                await _servers.TryDeleteBlobAsync(blobId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Chat {ChatId}: blob delete after receive failed", _chat.Id);
        }

        return true;
    }

    private async Task ReceiptBotBlobAsync(ChatMessageEntity row, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.TransferId))
            return;
        var hint = LooksLikeHttpBaseUrl(row.TransferHost) ? row.TransferHost : null;
        await _servers.TrySubmitBotMessageReceiptAsync(row.TransferId.Trim(), hint, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Bot offers store the ciphertext SHA-256 in the token. A TCP token must not be checked as a hash.</summary>
    private static string? CiphertextShaOrNull(string? token)
    {
        var value = token?.Trim() ?? "";
        if (value.Length != 64)
            return null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var hex = (c >= '0' && c <= '9') ||
                      (c >= 'a' && c <= 'f') ||
                      (c >= 'A' && c <= 'F');
            if (!hex)
                return null;
        }

        return value;
    }

    private static bool LooksLikeHttpBaseUrl(string? host) =>
        host is not null && !string.IsNullOrWhiteSpace(host) &&
        (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
         host.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    /// <summary>Updates the cached chat row from DB (same Id) without creating a new session.</summary>
    public void ApplyChatRow(ChatEntity row)
    {
        if (row == null)
            throw new ArgumentNullException(nameof(row));
        if (row.Id != _chat.Id)
            throw new ArgumentException("Chat id mismatch.", nameof(row));

        var hadPeerKey = !string.IsNullOrWhiteSpace(_chat.PeerRsaPublicJson);
        _chat = row;
        // Peer key often lands via long-poll after ChatRequest — re-queue Pending sends.
        if (!hadPeerKey && !string.IsNullOrWhiteSpace(_chat.PeerRsaPublicJson))
            _ = EnqueueExistingPendingAsync(CancellationToken.None);
    }

    /// <summary>
    /// UI refresh when delivery status changes outside this session (e.g. server delivery receipt).
    /// </summary>
    public void NotifyMessagesChangedFromExternal() => RaiseMessagesChanged();

    public Task IngestIncomingWireFromServerAsync(
        byte[] wire,
        CancellationToken cancellationToken,
        string? serverBaseUrl = null) =>
        Task.CompletedTask;

    /// <summary>Fx48: no mesh handshake; re-queue pending sends after a peer ChatRequest resync.</summary>
    public Task HandlePeerInviteResyncAsync(CancellationToken cancellationToken = default) =>
        EnqueueExistingPendingAsync(cancellationToken);

    /// <summary>TRL-7: push local AboutMe + Avatar via messenger server (not stored in chat history).</summary>
    public async Task SendLocalUserInfoAsync(CancellationToken cancellationToken = default)
    {
        var latest = await _repo.GetChatAsync(_chat.Id).ConfigureAwait(false);
        if (latest != null)
            ApplyChatRow(latest);

        var about = _user.AboutMe ?? "";
        var wire = ChatWireCodec.EncodeUserInfo(about, _user.Avatar ?? Array.Empty<byte>());
        var acceptedId = await _servers
            .TryDeliverWireAsync(_chat, _user, wire, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(acceptedId))
        {
            throw new InvalidOperationException(
                "Messenger server did not accept UserInfo (no peer key or no ready server).");
        }
    }

    /// <summary>
    /// For human peers: publish ChatRequest so PeerRsaPublicJson can land via long-poll.
    /// Bot chats skip ChatRequest (catalog key is already on the row). Then re-queues Pending sends.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var latest = await _repo.GetChatAsync(_chat.Id).ConfigureAwait(false);
        if (latest != null)
            ApplyChatRow(latest);

        if (MessengerServerSyncService.NeedsBotCatalogLookup(_chat))
        {
            var ensured = await _servers
                .TryEnsureBotChatFromCatalogAsync(_chat.Id, cancellationToken)
                .ConfigureAwait(false);
            if (ensured != null)
                ApplyChatRow(ensured);
        }

        _logger?.LogDebug(
            "Chat {ChatId}: StartAsync peer={PeerId} IsBot={IsBot} hasPeerKey={HasKey} catalogChecked={Checked}",
            _chat.Id,
            _chat.PeerNetworkIdShort,
            _chat.IsBot,
            !string.IsNullOrWhiteSpace(_chat.PeerRsaPublicJson),
            _chat.BotCatalogChecked);

        // Also for bots: ChatRequest stores our RSA public key so the bot can encrypt replies.
        try
        {
            await _servers.PublishChatRequestAsync(_chat.PeerNetworkIdShort, cancellationToken)
                .ConfigureAwait(false);
            _logger?.LogDebug("Chat {ChatId}: PublishChatRequest sent for {PeerId} IsBot={IsBot}",
                _chat.Id, _chat.PeerNetworkIdShort, _chat.IsBot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Chat {ChatId}: PublishChatRequest on start failed", _chat.Id);
        }

        await EnqueueExistingPendingAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (text.Length > MaxMessageChars)
            throw new ArgumentException(
                $"Message is too long. Max length is {MaxMessageChars} characters.",
                nameof(text));

        var messageId = await _repo
            .AddMessageAsync(_chat.Id, true, text, MessageDeliveryStatus.Pending)
            .ConfigureAwait(false);
        RaiseMessagesChanged();

        _logger?.LogDebug(
            "Chat {ChatId}: SendText queued messageId={MessageId} chars={Len} IsBot={IsBot} peer={PeerId}",
            _chat.Id, messageId, text.Length, _chat.IsBot, _chat.PeerNetworkIdShort);

        // Await flush so WinForms SendAsync sees delivery errors and debugger can step into deliver.
        await EnqueueAndFlushAsync(messageId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendImageAsync(ReadOnlyMemory<byte> imageBytes, string mimeType,
        CancellationToken cancellationToken = default)
    {
        if (imageBytes.Length == 0)
            throw new ArgumentException("Image is empty.", nameof(imageBytes));
        if (string.IsNullOrWhiteSpace(mimeType))
            throw new ArgumentException("MIME type is required.", nameof(mimeType));

        var bytes = imageBytes.ToArray();
        var messageId = await _repo
            .AddImageMessageAsync(_chat.Id, true, mimeType.Trim(), bytes, MessageDeliveryStatus.Pending)
            .ConfigureAwait(false);
        RaiseMessagesChanged();
        QueueOutgoingDelivery(messageId);
    }

    public async ValueTask SendFileAsync(string fileName, ReadOnlyMemory<byte> fileBytes, string mimeType,
        CancellationToken cancellationToken = default)
    {
        if (fileBytes.Length == 0)
            throw new ArgumentException("File is empty.", nameof(fileBytes));
        if (string.IsNullOrWhiteSpace(mimeType))
            throw new ArgumentException("MIME type is required.", nameof(mimeType));

        var bytes = fileBytes.ToArray();
        var safeName = Path.GetFileName((fileName ?? "").Trim());
        if (string.IsNullOrEmpty(safeName))
            safeName = "file";

        var payloadKind = mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            ? "voice"
            : mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                ? "video"
                : "document";

        var messageId = await _repo
            .AddFileMessageAsync(_chat.Id, true, safeName, mimeType.Trim(), bytes, MessageDeliveryStatus.Pending)
            .ConfigureAwait(false);
        await _repo.UpdateMessageTransferMetadataAsync(
                messageId, "", "", payloadKind, safeName, bytes.Length, "", 0, 0, ChatTransferState.None)
            .ConfigureAwait(false);
        RaiseMessagesChanged();
        QueueOutgoingDelivery(messageId);
    }

    public async ValueTask RetryFailedMessageAsync(int messageId, CancellationToken cancellationToken = default)
    {
        var row = await _repo.GetMessageAsync(messageId, includePayloadBlob: false).ConfigureAwait(false);
        if (row == null || row.ChatId != _chat.Id || !row.Outgoing)
            return;

        await _repo.UpdateMessageDeliveryStatusAsync(messageId, MessageDeliveryStatus.Pending)
            .ConfigureAwait(false);
        RaiseMessagesChanged();
        QueueOutgoingDelivery(messageId);
    }

    /// <summary>
    /// Enqueue for the single per-chat outbound worker. Wire is rebuilt from DB in the flush
    /// worker so retries stay consistent with the stored payload.
    /// </summary>
    public void QueueOutgoingDelivery(int messageId)
    {
        lock (_pendingSync)
        {
            if (!_pendingOutgoing.Contains(messageId))
                _pendingOutgoing.Add(messageId);
        }

        StartFlushPendingInBackground();
    }

    private async Task EnqueueAndFlushAsync(int messageId, CancellationToken cancellationToken)
    {
        lock (_pendingSync)
        {
            if (!_pendingOutgoing.Contains(messageId))
                _pendingOutgoing.Add(messageId);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _outboundCts.Token);
        await TryFlushPendingOutgoingAsync(linked.Token).ConfigureAwait(false);
    }

    private async Task EnqueueExistingPendingAsync(CancellationToken cancellationToken)
    {
        var rows = await _repo.ListMessagesAsync(_chat.Id).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var row in rows)
        {
            if (!row.Outgoing || row.DeliveryStatus != (int)MessageDeliveryStatus.Pending)
                continue;
            QueueOutgoingDelivery(row.Id);
        }
    }

    private void StartFlushPendingInBackground()
    {
        if (Interlocked.CompareExchange(ref _flushWorkerRunning, 1, 0) != 0)
            return;

        var token = _outboundCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await TryFlushPendingOutgoingAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Chat {ChatId}: outbound flush worker stopped", _chat.Id);
            }
            finally
            {
                Interlocked.Exchange(ref _flushWorkerRunning, 0);
                // More items may have been queued while we were finishing.
                lock (_pendingSync)
                {
                    if (_pendingOutgoing.Count > 0 && !_outboundCts.IsCancellationRequested)
                        StartFlushPendingInBackground();
                }
            }
        }, CancellationToken.None);
    }

    private async Task TryFlushPendingOutgoingAsync(CancellationToken cancellationToken)
    {
        await _flushPendingSem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int nextId;
                lock (_pendingSync)
                {
                    if (_pendingOutgoing.Count == 0)
                        return;
                    nextId = _pendingOutgoing[0];
                }

                var row = await _repo.GetMessageAsync(nextId, includePayloadBlob: false).ConfigureAwait(false);
                if (row == null || row.ChatId != _chat.Id || !row.Outgoing)
                {
                    DequeuePendingOutgoingHead(nextId);
                    continue;
                }

                if (row.DeliveryStatus is (int)MessageDeliveryStatus.Sent
                    or (int)MessageDeliveryStatus.Delivered)
                {
                    DequeuePendingOutgoingHead(nextId);
                    continue;
                }

                try
                {
                    // Image/File rows need ImageBlob on the wire. The metadata-only fetch above
                    // leaves ImageBlob null, which made BuildOutgoingWire fall back to EncodeText
                    // (filename only) — receivers saw names with no downloadable payload.
                    if (row.PayloadKind is (int)ChatPayloadKind.Image or (int)ChatPayloadKind.File &&
                        row.ImageBlob is not { Length: > 0 })
                    {
                        var withBlob = await _repo.GetMessageAsync(nextId, includePayloadBlob: true)
                            .ConfigureAwait(false);
                        if (withBlob == null || withBlob.ChatId != _chat.Id || !withBlob.Outgoing)
                        {
                            DequeuePendingOutgoingHead(nextId);
                            continue;
                        }

                        row = withBlob;
                    }

                    var wire = BuildOutgoingWire(row);
                    await DeliverOutgoingWireAsync(nextId, wire, cancellationToken).ConfigureAwait(false);
                    DequeuePendingOutgoingHead(nextId);
                }
                catch (OperationCanceledException)
                {
                    // Keep Pending at head for a later retry.
                    throw;
                }
                catch (OutboundNotReadyException ex)
                {
                    // No peer key / no ready server yet — leave Pending in DB, drop from the
                    // in-memory queue so we do not spin. ApplyChatRow / StartAsync re-enqueue.
                    _logger?.LogDebug(ex,
                        "Chat {ChatId}: deferring outbound message {MessageId} IsBot={IsBot}",
                        _chat.Id, nextId, _chat.IsBot);
                    DequeuePendingOutgoingHead(nextId);
                    if (_chat.IsBot)
                        throw;
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex,
                        "Chat {ChatId}: outbound delivery failed for message {MessageId} IsBot={IsBot}",
                        _chat.Id, nextId, _chat.IsBot);
                    try
                    {
                        await _repo
                            .UpdateMessageDeliveryStatusAsync(nextId, MessageDeliveryStatus.Failed)
                            .ConfigureAwait(false);
                        RaiseMessagesChanged();
                    }
                    catch (Exception updateEx)
                    {
                        _logger?.LogDebug(updateEx,
                            "Chat {ChatId}: failed to mark message {MessageId} as failed",
                            _chat.Id, nextId);
                    }

                    DequeuePendingOutgoingHead(nextId);
                    // Surface to SendTextAsync / ChatForm so the user sees why it did not leave.
                    throw;
                }
            }
        }
        finally
        {
            _flushPendingSem.Release();
        }
    }

    private void DequeuePendingOutgoingHead(int messageId)
    {
        lock (_pendingSync)
        {
            if (_pendingOutgoing.Count > 0 && _pendingOutgoing[0] == messageId)
                _pendingOutgoing.RemoveAt(0);
        }
    }

    private static byte[] BuildOutgoingWire(ChatMessageEntity row) =>
        row.PayloadKind switch
        {
            (int)ChatPayloadKind.Image when row.ImageBlob is { Length: > 0 } =>
                ChatWireCodec.EncodeImage(row.MimeType, row.ImageBlob),
            (int)ChatPayloadKind.File when row.ImageBlob is { Length: > 0 } =>
                ChatWireCodec.EncodeFile(row.Text, row.MimeType, row.ImageBlob),
            (int)ChatPayloadKind.Image or (int)ChatPayloadKind.File =>
                throw new InvalidOperationException(
                    $"Outgoing attachment {row.Id} has no payload blob; refusing to send filename as text."),
            _ => ChatWireCodec.EncodeText(row.Text)
        };

    private async Task DeliverOutgoingWireAsync(int messageId, byte[] wire, CancellationToken cancellationToken)
    {
        // Refresh chat row so PeerRsaPublicJson / IsBot from catalog or long-poll is current.
        var latest = await _repo.GetChatAsync(_chat.Id).ConfigureAwait(false);
        if (latest != null)
            ApplyChatRow(latest);

        // Existing rows may have been created as human (no IsBot / no catalog key).
        if (MessengerServerSyncService.NeedsBotCatalogLookup(_chat))
        {
            var ensured = await _servers
                .TryEnsureBotChatFromCatalogAsync(_chat.Id, cancellationToken)
                .ConfigureAwait(false);
            if (ensured != null)
                ApplyChatRow(ensured);
        }

        _logger?.LogDebug(
            "Chat {ChatId}: DeliverOutgoing messageId={MessageId} wireLen={WireLen} IsBot={IsBot} peer={PeerId} hasPeerKey={HasKey} catalogChecked={Checked}",
            _chat.Id,
            messageId,
            wire.Length,
            _chat.IsBot,
            _chat.PeerNetworkIdShort,
            !string.IsNullOrWhiteSpace(_chat.PeerRsaPublicJson),
            _chat.BotCatalogChecked);

        string? acceptedId;
        if (_chat.IsBot)
        {
            acceptedId = await _servers
                .TryDeliverBotWireAsync(_chat, _user, wire, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(acceptedId))
            {
                _logger?.LogError(
                    "Chat {ChatId}: bot deliver returned null for messageId={MessageId} peer={PeerId}",
                    _chat.Id, messageId, _chat.PeerNetworkIdShort);
                throw new InvalidOperationException(
                    "Не удалось доставить сообщение боту через messenger-сервер.");
            }
        }
        else
        {
            acceptedId = await _servers
                .TryDeliverWireAsync(_chat, _user, wire, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(acceptedId))
            {
                _logger?.LogDebug(
                    "Chat {ChatId}: human deliver not ready for messageId={MessageId} peer={PeerId}",
                    _chat.Id, messageId, _chat.PeerNetworkIdShort);
                throw new OutboundNotReadyException(
                    "Messenger server did not accept the message (no peer key or no ready server).");
            }
        }

        _logger?.LogDebug(
            "Chat {ChatId}: deliver OK messageId={MessageId} serverMessageId={ServerMessageId} IsBot={IsBot}",
            _chat.Id, messageId, acceptedId, _chat.IsBot);

        await _repo.RegisterOutgoingServerMessageAsync(acceptedId!, messageId, _chat.Id)
            .ConfigureAwait(false);
        await _repo.UpdateMessageDeliveryStatusAsync(messageId, MessageDeliveryStatus.Sent)
            .ConfigureAwait(false);
        RaiseMessagesChanged();
    }

    private void RaiseMessagesChanged()
    {
        if (_uiSynchronizationContext != null)
            _uiSynchronizationContext.Post(_ => MessagesChanged?.Invoke(this, EventArgs.Empty), null);
        else
            MessagesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Temporary: peer key or messenger target not ready; keep DB status Pending.</summary>
    private sealed class OutboundNotReadyException(string message) : Exception(message);
}
