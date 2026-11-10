namespace ShortP2P.Messenger;

/// <summary>
///     Параметры бэкенда мессенджера. Лимит бинарного сообщения можно менять без смены транспорта.
/// </summary>
public sealed class MessengerOptions
{
    /// <summary>
    ///     Максимальный размер расшифрованного бинарного сообщения (байт).
    /// </summary>
    public int MaxBinaryMessageBytes { get; set; } = 1048576;

    /// <summary>
    ///     Если сборка входящего сообщения зависла дольше этого времени, отправляется NACK с недостающими чанками.
    /// </summary>
    public TimeSpan ReassemblyTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Как долго хранить у отправителя шифр-чанки для возможной дозапросной отправки по NACK.
    /// </summary>
    public TimeSpan OutboundChunkCacheTtl { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     Верхняя граница количества индексов чанков в одном NACK.
    /// </summary>
    public int MaxNackChunkIndices { get; set; } = 96;

    /// <summary>
    ///     How long <c>SendBinaryAsyncExpectAck</c> waits for the peer delivery ACK.
    ///     LoRa / slow links need tens of seconds; default 30 s.
    /// </summary>
    public TimeSpan DeliveryAckTimeout { get; set; } = TimeSpan.FromSeconds(30);
}