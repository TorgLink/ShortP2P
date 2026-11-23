using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShortP2P.MessengerServer.Api.Extensions;
using ShortP2P.MessengerServer.Contracts;
using ShortP2P.MessengerServer.Contracts.Dtos;
using ShortP2P.MessengerServer.UseCases;
using ShortP2P.MessengerServer.UseCases.Messages;

namespace ShortP2P.MessengerServer.Api.Controllers;

[ApiController]
[Authorize]
[Route($"{ApiRoutes.Prefix}/messages")]
public sealed class MessagesController(
    SendMessageUseCase sendMessageUseCase,
    SubmitDeliveryReceiptUseCase submitReceiptUseCase,
    GetDeliveryReceiptsUseCase getReceiptsUseCase) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> SendMessageAsync(
        [FromBody] MessageDto message,
        CancellationToken cancellationToken)
    {
        try
        {
            var caller = HttpContext.RequireNetworkId();
            if (!string.Equals(message.SrcNetworkId, caller, StringComparison.Ordinal))
                throw UseCaseException.Unauthorized("srcNetworkId must match the authenticated client.");

            await sendMessageUseCase
                .ExecuteAsync(new SendMessageCommand(message.ToDomain()), cancellationToken)
                .ConfigureAwait(false);

            return Accepted();
        }
        catch (UseCaseException ex)
        {
            return this.ToApiErrorResult(ex);
        }
    }

    [HttpPost("receipts")]
    public async Task<IActionResult> SubmitReceiptAsync(
        [FromBody] DeliveryReceiptRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var caller = HttpContext.RequireNetworkId();
            var deviceId = HttpContext.RequireDeviceId();
            await submitReceiptUseCase
                .ExecuteAsync(
                    new SubmitDeliveryReceiptCommand(
                        caller,
                        deviceId,
                        request.MessageId,
                        request.ReceivedAtUtc,
                        NotifySender: request.NotifySender ?? true),
                    cancellationToken)
                .ConfigureAwait(false);

            return Accepted();
        }
        catch (UseCaseException ex)
        {
            return this.ToApiErrorResult(ex);
        }
    }

    [HttpGet("receipts")]
    public async Task<IActionResult> GetReceiptsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var caller = HttpContext.RequireNetworkId();
            var list = await getReceiptsUseCase
                .ExecuteAsync(new GetDeliveryReceiptsQuery(caller), cancellationToken)
                .ConfigureAwait(false);

            return Ok(list.Select(x => x.ToDto()).ToArray());
        }
        catch (UseCaseException ex)
        {
            return this.ToApiErrorResult(ex);
        }
    }
}
