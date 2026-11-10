using System.Collections.Concurrent;
using System.Security.Cryptography;
using ShortP2P.Crypto;
using ShortP2P.Transport.Abstractions;

namespace ShortP2P.Messenger;

/// <summary>
///     Бэкенд мессенджера на устройстве пользователя: шифрует бинарные сообщения (с фрагментацией под лимит
///     <see cref="P2PSession" />)
///     и собирает входящие фрагменты обратно. Поддерживает квитанции доставки, дедупликацию по id сообщения
///     и отбрасывание пакетов с id исходящих сообщений этого клиента (эхо на тот же ключ сессии).
/// </summary>
public sealed class MessengerService(
    Func<ReadOnlyMemory<byte>, TransportAddress, CancellationToken, ValueTask> sendCipherAsync,
    Func<CancellationToken, ValueTask<P2PSession>> sessionProvider,
    MessengerOptions? options = null,
    Func<ValueTask>? onDecryptFailure = null)
    : IAsyncDisposable
{
    private const int MaxTrackedDeliveredIds = 8192;
    private const int MaxTrackedOutboundIds = 8192;
    private static readonly TimeSpan NackMinInterval = TimeSpan.FromMilliseconds(500);
    private readonly ConcurrentDictionary<Guid, AckWaiter> _ackWaiters = new();
    private readonly HashSet<Guid> _deliveredMessageIds = [];

    private readonly MessengerOptions _options = options ?? new MessengerOptions();
    private readonly Dictionary<Guid, OutboundCacheEntry> _outboundChunks = new();

    /// <summary>Идентификаторы сообщений, отправленных этим экземпляром (защита от приёма собственного эха/hairpin).</summary>
    private readonly HashSet<Guid> _ownOutboundMessageIds = [];

    private readonly Dictionary<Guid, Reassembly> _pending = new();

    private readonly Func<ReadOnlyMemory<byte>, TransportAddress, CancellationToken, ValueTask> _sendCipherAsync =
        sendCipherAsync ?? throw new ArgumentNullException(nameof(sendCipherAsync));

    private readonly Func<CancellationToken, ValueTask<P2PSession>> _sessionProvider =
        sessionProvider ?? throw new ArgumentNullException(nameof(sessionProvider));

    private readonly object _sync = new();

    private CancellationTokenSource? _cts;

    public ValueTask DisposeAsync()
    {
        return StopAsync();
    }

    public event EventHandler<IncomingBinaryMessage>? GotData;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cts != null) return ValueTask.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Передаёт уже снятый с префикса 0x02 cipher payload в очередь обработки.
    ///     Вызывается из подписки на <c>MessageTransceiver.GotData</c>.
    /// </summary>
    public bool TryAcceptCipher(  TransportReceiveMessage message)
    {
        var token = _cts?.Token ?? CancellationToken.None;
        if (token.IsCancellationRequested)
            return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessIncomingPacketAsync(message, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on stop
            }
        }, token);
        return true;
    }

    public async ValueTask SendBinaryAsync(byte[] data, TransportAddress destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var messageId = Guid.NewGuid();
        await SendChunksForMessageAsync(data, messageId, destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Encrypts once, fans the chunks out to every destination, then waits for the peer delivery ACK.
    ///     Failures on individual destinations are tolerated as long as at least one send path succeeds;
    ///     missing ACK within <see cref="MessengerOptions.DeliveryAckTimeout"/> fails the call.
    /// </summary>
    public async ValueTask SendBinaryAsyncExpectAck(byte[] data, IReadOnlyList<TransportAddress> destinationsInOrder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (destinationsInOrder.Count == 0)
            throw new ArgumentException("At least one destination is required.", nameof(destinationsInOrder));

        if (data.Length > _options.MaxBinaryMessageBytes)
            throw new ArgumentException($"Message exceeds MaxBinaryMessageBytes ({_options.MaxBinaryMessageBytes}).",
                nameof(data));

        var messageId = Guid.NewGuid();
        var waiter = new AckWaiter();
        _ackWaiters[messageId] = waiter;
        try
        {
            var prepared = await PrepareEncryptedChunksAsync(data, messageId, cancellationToken).ConfigureAwait(false);

            var sendErrors = new ConcurrentBag<Exception>();
            var sendTasks = destinationsInOrder.Select(async dest =>
            {
                try
                {
                    await SendPreparedChunksToDestinationAsync(prepared, messageId, dest, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sendErrors.Add(ex);
                }
            });

            await Task.WhenAll(sendTasks).ConfigureAwait(false);

            if (sendErrors.Count >= destinationsInOrder.Count)
                throw new IOException(
                    $"Failed to send message on all {destinationsInOrder.Count} destination(s).",
                    sendErrors.ToArray()[0]);

            CacheOutboundChunks(messageId, destinationsInOrder[0], prepared);

            try
            {
                await waiter.Task.WaitAsync(_options.DeliveryAckTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"Delivery ACK for {messageId:N} not received within {_options.DeliveryAckTimeout.TotalSeconds:0}s.");
            }
        }
        finally
        {
            _ackWaiters.TryRemove(messageId, out _);
        }
    }

    private async ValueTask SendChunksForMessageAsync(byte[] data, Guid messageId, TransportAddress destination,
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareEncryptedChunksAsync(data, messageId, cancellationToken).ConfigureAwait(false);
        await SendPreparedChunksToDestinationAsync(prepared, messageId, destination, cancellationToken)
            .ConfigureAwait(false);
        CacheOutboundChunks(messageId, destination, prepared);
    }

    private async ValueTask<byte[][]> PrepareEncryptedChunksAsync(byte[] data, Guid messageId,
        CancellationToken cancellationToken)
    {
        if (data.Length > _options.MaxBinaryMessageBytes)
            throw new ArgumentException($"Message exceeds MaxBinaryMessageBytes ({_options.MaxBinaryMessageBytes}).",
                nameof(data));

        var session = await _sessionProvider(cancellationToken).ConfigureAwait(false);
        var maxPayload = ChunkCodec.MaxPayloadPerChunk(session);
        var totalChunks = data.Length == 0 ? 1 : (data.Length + maxPayload - 1) / maxPayload;

        lock (_sync)
        {
            if (_ownOutboundMessageIds.Count >= MaxTrackedOutboundIds)
                _ownOutboundMessageIds.Clear();
            _ownOutboundMessageIds.Add(messageId);
        }

        var encryptedChunks = new byte[totalChunks][];
        for (var i = 0; i < totalChunks; i++)
        {
            var offset = i * maxPayload;
            var len = Math.Min(maxPayload, data.Length - offset);
            var sliceBytes = len == 0 ? Array.Empty<byte>() : new byte[len];
            if (len > 0)
                Buffer.BlockCopy(data, offset, sliceBytes, 0, len);
            var plain = ChunkCodec.BuildChunk(messageId, i, totalChunks, sliceBytes);
            encryptedChunks[i] = session.Encrypt(plain);
        }

        return encryptedChunks;
    }

    private async Task SendPreparedChunksToDestinationAsync(byte[][] encryptedChunks, Guid messageId,
        TransportAddress destination, CancellationToken cancellationToken)
    {
        for (var i = 0; i < encryptedChunks.Length; i++)
            await _sendCipherAsync(encryptedChunks[i], destination, cancellationToken).ConfigureAwait(false);
    }

    private void CacheOutboundChunks(Guid messageId, TransportAddress destination, byte[][] encryptedChunks)
    {
        lock (_sync)
        {
            _outboundChunks[messageId] = new OutboundCacheEntry(destination, encryptedChunks);
        }
    }

    private async Task ProcessIncomingPacketAsync(TransportReceiveMessage msg, CancellationToken cancellationToken)
    {
        List<(Guid MessageId, TransportAddress ReplyTo, int[] MissingIndices)> nacksToSend;
        lock (_sync)
        {
            nacksToSend = SweepStateAndCollectNacksLocked(DateTimeOffset.UtcNow);
        }

        foreach (var nack in nacksToSend)
            _ = SendDeliveryNackAsync(nack.MessageId, nack.MissingIndices, nack.ReplyTo, cancellationToken);

        var session = await _sessionProvider(cancellationToken).ConfigureAwait(false);
        byte[] decrypted;
        try
        {
            decrypted = session.Decrypt(msg.Payload.ToArray());
        }
        catch (CryptographicException)
        {
            ScheduleDecryptRecovery();
            return;
        }

        if (DeliveryAckCodec.TryParse(decrypted, out var ackId))
        {
            lock (_sync)
            {
                _outboundChunks.Remove(ackId);
            }

            if (_ackWaiters.TryGetValue(ackId, out var w))
                w.TrySetResult();
            return;
        }

        if (DeliveryNackCodec.TryParse(decrypted, out var nackMessageId, out var missingChunkIndices))
        {
            if (_ackWaiters.TryGetValue(nackMessageId, out var waiter))
                waiter.MarkNackObserved();
            await ResendMissingChunksAsync(nackMessageId, missingChunkIndices, msg.RemoteAddress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!TryParseChunk(decrypted, out var messageId, out var chunkIndex, out var totalChunks,
                out var payloadBytes)) return;

        var sendDeliveryAck = false;
        byte[]? assembledBuffer = null;
        var now = DateTimeOffset.UtcNow;
        int[]? missing = null;

        lock (_sync)
        {
            if (_ownOutboundMessageIds.Contains(messageId))
                return;

            if (!_pending.TryGetValue(messageId, out var state))
            {
                state = new Reassembly(totalChunks, msg.RemoteAddress);
                _pending[messageId] = state;
            }

            if (state.TotalChunks != totalChunks)
            {
                _pending.Remove(messageId);
                return;
            }

            if (state.Parts[chunkIndex] != null)
                return;

            var prevUpdateUtc = state.LastUpdatedUtc;
            state.Parts[chunkIndex] = payloadBytes;
            state.Received++;
            state.LastUpdatedUtc = now;

            sendDeliveryAck = state.Received == state.TotalChunks;
            if (sendDeliveryAck)
            {
                _pending.Remove(messageId);

                var fullLen = 0;
                for (var i = 0; i < state.TotalChunks; i++)
                    fullLen += state.Parts[i]!.Length;

                var buffer = new byte[fullLen];
                var pos = 0;
                for (var i = 0; i < state.TotalChunks; i++)
                {
                    var part = state.Parts[i]!;
                    Buffer.BlockCopy(part, 0, buffer, pos, part.Length);
                    pos += part.Length;
                }

                if (buffer.Length <= _options.MaxBinaryMessageBytes)
                    assembledBuffer = buffer;
            }
            else if (now - prevUpdateUtc >= _options.ReassemblyTimeout &&
                     now - state.LastNackUtc >= NackMinInterval)
            {
                missing = CollectMissingChunkIndices(state);
                state.LastNackUtc = now;
            }

            if (!sendDeliveryAck && !state.NackCheckScheduled)
            {
                state.NackCheckScheduled = true;
                _ = ScheduleReassemblyNackChecksAsync(messageId, msg.RemoteAddress,
                    _cts?.Token ?? CancellationToken.None);
            }
        }

        if (assembledBuffer != null)
        {
            var shouldEnqueue = TrackDeliverOnce(messageId);
            if (shouldEnqueue)
                GotData?.Invoke(this, new IncomingBinaryMessage(assembledBuffer, msg.RemoteAddress));
        }

        if (missing is { Length: > 0 })
            _ = SendDeliveryNackAsync(messageId, missing, msg.RemoteAddress, cancellationToken);

        if (sendDeliveryAck)
            _ = SendDeliveryAckAsync(messageId, msg.RemoteAddress, cancellationToken);
    }

    private bool TrackDeliverOnce(Guid messageId)
    {
        if (_deliveredMessageIds.Contains(messageId))
            return false;
        if (_deliveredMessageIds.Count >= MaxTrackedDeliveredIds)
            _deliveredMessageIds.Clear();
        _deliveredMessageIds.Add(messageId);
        return true;
    }

    private async Task SendDeliveryAckAsync(Guid messageId, TransportAddress replyTo,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = await _sessionProvider(cancellationToken).ConfigureAwait(false);
            var plain = DeliveryAckCodec.ToBytes(messageId);
            var encrypted = session.Encrypt(plain);
            await _sendCipherAsync(encrypted, replyTo, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // ignore: доставка ack best-effort
        }
    }

    private async Task SendDeliveryNackAsync(Guid messageId, int[] missingChunkIndices, TransportAddress replyTo,
        CancellationToken cancellationToken)
    {
        if (missingChunkIndices.Length == 0)
            return;
        try
        {
            var session = await _sessionProvider(cancellationToken).ConfigureAwait(false);
            var plain = DeliveryNackCodec.ToBytes(messageId, missingChunkIndices);
            var encrypted = session.Encrypt(plain);
            await _sendCipherAsync(encrypted, replyTo, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
    }

    private async ValueTask ResendMissingChunksAsync(Guid messageId, int[] missingChunkIndices,
        TransportAddress replyTo,
        CancellationToken cancellationToken)
    {
        byte[][]? encryptedChunks = null;
        lock (_sync)
        {
            if (_outboundChunks.TryGetValue(messageId, out var cached))
            {
                cached.LastAccessUtc = DateTimeOffset.UtcNow;
                encryptedChunks = cached.EncryptedChunks;
            }
        }

        if (encryptedChunks == null)
            return;

        foreach (var idx in missingChunkIndices)
        {
            if (idx < 0 || idx >= encryptedChunks.Length)
                continue;
            var packet = encryptedChunks[idx];
            if (packet.Length == 0)
                continue;
            await _sendCipherAsync(packet, replyTo, cancellationToken).ConfigureAwait(false);
        }
    }

    private int[] CollectMissingChunkIndices(Reassembly state)
    {
        var result = new List<int>(Math.Min(state.TotalChunks - state.Received, _options.MaxNackChunkIndices));
        for (var i = 0; i < state.TotalChunks && result.Count < _options.MaxNackChunkIndices; i++)
            if (state.Parts[i] == null)
                result.Add(i);

        return result.ToArray();
    }

    private List<(Guid MessageId, TransportAddress ReplyTo, int[] MissingIndices)> SweepStateAndCollectNacksLocked(
        DateTimeOffset nowUtc)
    {
        var nacks = new List<(Guid, TransportAddress, int[])>();

        foreach (var kv in _pending.ToArray())
        {
            var state = kv.Value;
            var idleFor = nowUtc - state.LastUpdatedUtc;
            if (idleFor >= _options.ReassemblyTimeout && nowUtc - state.LastNackUtc >= NackMinInterval)
            {
                var missing = CollectMissingChunkIndices(state);
                if (missing.Length > 0)
                {
                    state.LastNackUtc = nowUtc;
                    nacks.Add((kv.Key, state.RemoteAddress, missing));
                }
            }

            if (idleFor >= _options.ReassemblyTimeout + _options.ReassemblyTimeout)
                _pending.Remove(kv.Key);
        }

        foreach (var kv in _outboundChunks.ToArray())
            if (nowUtc - kv.Value.LastAccessUtc >= _options.OutboundChunkCacheTtl)
                _outboundChunks.Remove(kv.Key);

        return nacks;
    }

    private async Task ScheduleReassemblyNackChecksAsync(Guid messageId, TransportAddress replyTo,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.ReassemblyTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            int[] missing;
            lock (_sync)
            {
                if (!_pending.TryGetValue(messageId, out var state))
                    return;

                var now = DateTimeOffset.UtcNow;
                var idleFor = now - state.LastUpdatedUtc;
                if (idleFor >= _options.ReassemblyTimeout + _options.ReassemblyTimeout)
                {
                    _pending.Remove(messageId);
                    return;
                }

                if (state.Received >= state.TotalChunks)
                {
                    _pending.Remove(messageId);
                    return;
                }

                if (now - state.LastNackUtc < NackMinInterval)
                    continue;

                missing = CollectMissingChunkIndices(state);
                state.LastNackUtc = now;
            }

            if (missing.Length > 0)
                _ = SendDeliveryNackAsync(messageId, missing, replyTo, cancellationToken);
        }
    }

    private static bool TryParseChunk(byte[] decrypted, out Guid messageId, out int chunkIndex, out int totalChunks,
        out byte[] payloadBytes)
    {
        try
        {
            ChunkCodec.ParseChunk(decrypted, out messageId, out chunkIndex, out totalChunks, out var payload);
            payloadBytes = payload.ToArray();
            return true;
        }
        catch (CryptographicException)
        {
            messageId = Guid.Empty;
            chunkIndex = 0;
            totalChunks = 0;
            payloadBytes = [];
            return false;
        }
    }

    /// <summary>Вне потока приёма: иначе StopAsync мессенджера может взаимно заблокироваться с ReceiveLoop.</summary>
    private void ScheduleDecryptRecovery()
    {
        var cb = onDecryptFailure;
        if (cb == null)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await cb().ConfigureAwait(false);
            }
            catch
            {
                // сбой восстановления — игнорируем
            }
        });
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts == null) return;
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            await _cts.CancelAsync();
        }

        _cts.Dispose();
        _cts = null;

        foreach (var kv in _ackWaiters.ToArray())
            kv.Value.TrySetCanceled();
        _ackWaiters.Clear();
        lock (_sync)
        {
            _pending.Clear();
            _outboundChunks.Clear();
        }
    }

    private sealed class Reassembly(int totalChunks, TransportAddress remoteAddress)
    {
        public int TotalChunks { get; } = totalChunks;
        public TransportAddress RemoteAddress { get; } = remoteAddress;

        public byte[][] Parts { get; } = new byte[totalChunks][];

        public int Received { get; set; }
        public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastNackUtc { get; set; } = DateTimeOffset.MinValue;
        public bool NackCheckScheduled { get; set; }
    }

    private sealed class OutboundCacheEntry(TransportAddress destination, byte[][] encryptedChunks)
    {
        public TransportAddress Destination { get; } = destination;
        public byte[][] EncryptedChunks { get; } = encryptedChunks;
        public DateTimeOffset LastAccessUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class AckWaiter
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _nackObserved;

        public Task Task => _tcs.Task;
        public bool NackObserved => _nackObserved != 0;

        public void MarkNackObserved()
        {
            Interlocked.Exchange(ref _nackObserved, 1);
        }

        public void TrySetResult()
        {
            _tcs.TrySetResult();
        }

        public void TrySetCanceled()
        {
            _tcs.TrySetCanceled();
        }
    }
}