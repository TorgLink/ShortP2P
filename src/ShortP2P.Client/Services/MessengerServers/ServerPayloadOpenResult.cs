namespace ShortP2P.Client.Services.MessengerServers;

/// <summary>Result of opening a messenger-server envelope (chat wire and/or a control reply).</summary>
public sealed class ServerPayloadOpenResult
{
    private ServerPayloadOpenResult(byte[]? chatWire, byte[]? responseEnvelope)
    {
        ChatWire = chatWire;
        ResponseEnvelope = responseEnvelope;
    }

    /// <summary>Decrypted chat wire. Null for probe acknowledgements and re-key notices.</summary>
    public byte[]? ChatWire { get; }

    /// <summary>Envelope to send back (probe acknowledgement or re-key request). Not a chat message.</summary>
    public byte[]? ResponseEnvelope { get; }

    public bool ShouldPersistChat => ChatWire != null;

    public static ServerPayloadOpenResult Chat(byte[] chatWire, byte[]? responseEnvelope = null) =>
        new(chatWire, responseEnvelope);

    public static ServerPayloadOpenResult Control(byte[]? responseEnvelope = null) =>
        new(null, responseEnvelope);
}
