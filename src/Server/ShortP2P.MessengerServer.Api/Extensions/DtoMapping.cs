using ShortP2P.MessengerServer.Contracts.Dtos;
using ShortP2P.MessengerServer.Domain;
using ShortP2P.MessengerServer.UseCases;
using ShortP2P.MessengerServer.UseCases.Abstractions;
using ShortP2P.MessengerServer.UseCases.Presence;

namespace ShortP2P.MessengerServer.Api.Extensions;

public static class ApiResults
{
    public static IResult FromException(UseCaseException ex) =>
        Results.Json(
            new ApiError { Code = ex.Code, Message = ex.Message },
            statusCode: ex.Code switch
            {
                "Validation" => StatusCodes.Status400BadRequest,
                "Unauthorized" => StatusCodes.Status401Unauthorized,
                "NotFound" => StatusCodes.Status404NotFound,
                "Conflict" => StatusCodes.Status409Conflict,
                "PeerOffline" => StatusCodes.Status409Conflict,
                "PayloadTooLarge" => StatusCodes.Status413PayloadTooLarge,
                "Unavailable" => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status400BadRequest
            });
}

public static class DtoMapping
{
    public static Message ToDomain(this MessageDto dto) => new()
    {
        MessageId = dto.MessageId,
        SrcNetworkId = dto.SrcNetworkId,
        TgtNetworkId = dto.TgtNetworkId,
        CreatedUtc = DateTime.SpecifyKind(dto.CreatedUtc, DateTimeKind.Utc),
        UpdatedUtc = DateTime.SpecifyKind(dto.UpdatedUtc, DateTimeKind.Utc),
        EncryptedDataBase64 = dto.EncryptedDataBase64
    };

    public static MessageDto ToDto(this Message message) => new()
    {
        MessageId = message.MessageId,
        SrcNetworkId = message.SrcNetworkId,
        TgtNetworkId = message.TgtNetworkId,
        CreatedUtc = message.CreatedUtc,
        UpdatedUtc = message.UpdatedUtc,
        EncryptedDataBase64 = message.EncryptedDataBase64
    };

    public static ChatDto ToDto(this Chat chat) => new()
    {
        ChatId = chat.ChatId,
        NetworkIds = chat.NetworkIds,
        CreatedAtUtc = chat.CreatedAtUtc
    };

    public static ChatRequestDto ToDto(this ChatRequest request) => new()
    {
        NetworkId = request.RequesterNetworkId,
        PublicKey = request.PublicKey
    };

    public static DeliveryReceiptDto ToDto(this DeliveryTicket ticket) => new()
    {
        MessageId = ticket.MessageId,
        ReceivedAtUtc = ticket.ReceivedAtUtc
    };

    public static ServerCertificateResponse ToDto(this ServerCertificateInfo info) => new()
    {
        FingerprintSha256 = info.FingerprintSha256,
        Subject = info.Subject,
        NotAfterUtc = info.NotAfterUtc
    };

    public static ClientPresenceDto ToDto(this ClientPresenceInfo info) => new()
    {
        NetworkId = info.NetworkId,
        Nick = info.Nick,
        Status = info.Status == ClientOnlineStatus.Online
            ? ClientPresenceDto.StatusOnline
            : ClientPresenceDto.StatusOffline,
        LastSeenAtUtc = info.LastSeenAtUtc,
        VisibleForSearch = info.VisibleForSearch
    };

    public static ForwardDto ToDto(this ForwardEnvelope envelope) => new()
    {
        ForwardId = envelope.ForwardId,
        SrcNetworkId = envelope.SrcNetworkId,
        TgtNetworkId = envelope.TgtNetworkId,
        Kind = envelope.Kind switch
        {
            ShortP2P.MessengerServer.Domain.ForwardKind.PeerProfileRequest =>
                Contracts.Dtos.ForwardKind.PeerProfileRequest,
            ShortP2P.MessengerServer.Domain.ForwardKind.PeerProfileReply =>
                Contracts.Dtos.ForwardKind.PeerProfileReply,
            _ => Contracts.Dtos.ForwardKind.PeerProfileRequest
        },
        PayloadBase64 = envelope.PayloadBase64,
        CreatedUtc = envelope.CreatedUtc
    };
}
