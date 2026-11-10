namespace ShortP2P.Client.ChatMedia;

/// <summary>Dictionary-compressed text (SMT-3). <see cref="Payload"/> is caseFlag+token bit stream.</summary>
public sealed record ChatWireCompressedText(string Language, int DictVersion, byte[] Payload) : ChatWireMessage;
