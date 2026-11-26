namespace ShortP2P.MessengerServer.Contracts.Dtos;

/// <summary>Client submits a delivery receipt for a received message.</summary>
public sealed class DeliveryReceiptRequest
{
    public required string MessageId { get; init; }

    /// <summary>Receipt timestamp UTC.</summary>
    public required DateTime ReceivedAtUtc { get; init; }

    /// <summary>
    /// When false, the server removes this device's inbox copy without creating a delivery ticket
    /// for the sender. Use when the payload was not accepted as chat content (unknown session / re-key).
    /// Omitted or true keeps the previous notify-sender behaviour.
    /// </summary>
    public bool? NotifySender { get; init; }
}
