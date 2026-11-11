namespace ShortP2P.Client.ChatMedia;

/// <summary>Compact binary session crypto probe (not shown in chat history).</summary>
public sealed record ChatWireSessionProbe(byte Kind, byte[] Challenge) : ChatWireMessage;
