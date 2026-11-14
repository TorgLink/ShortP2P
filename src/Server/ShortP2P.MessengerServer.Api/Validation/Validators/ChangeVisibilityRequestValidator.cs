using FluentValidation;
using ShortP2P.MessengerServer.Contracts.Dtos;

namespace ShortP2P.MessengerServer.Api.Validation.Validators;

public sealed class ChangeVisibilityRequestValidator : AbstractValidator<ChangeVisibilityRequest>
{
    public ChangeVisibilityRequestValidator()
    {
        RuleFor(x => x.VisibleForSearch).NotNull();
    }
}
