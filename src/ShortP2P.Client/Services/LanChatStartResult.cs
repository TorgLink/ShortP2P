using ShortP2P.Client.Data;

namespace ShortP2P.Client.Services;

public sealed class LanChatStartResult
{
    public LanChatStartKind Kind { get; private init; }
    public ChatEntity? Chat { get; private init; }
    public string? Message { get; private init; }

    public static LanChatStartResult AlreadyExists(ChatEntity chat)
    {
        return new LanChatStartResult
        {
            Kind = LanChatStartKind.AlreadyExists,
            Chat = chat
        };
    }

    public static LanChatStartResult Created(ChatEntity chat)
    {
        return new LanChatStartResult
        {
            Kind = LanChatStartKind.Created,
            Chat = chat
        };
    }

    public static LanChatStartResult WaitingForPeer()
    {
        return new LanChatStartResult
        {
            Kind = LanChatStartKind.WaitingForPeer,
            Message =
                "Приглашение отправлено. Чат появится в списке, когда пир ответит (при необходимости откройте список чатов позже)."
        };
    }

    public static LanChatStartResult Failed(string message)
    {
        return new LanChatStartResult
        {
            Kind = LanChatStartKind.Failed,
            Message = message
        };
    }
}
