using FluentValidation;

namespace AuthService.Domain.DTOs.User
{
    public class GetUsersRequestValidator : AbstractValidator<GetUsersRequest>
    {
        public GetUsersRequestValidator()
        {
            RuleFor(x => x.Page)
                .GreaterThanOrEqualTo(1)
                .WithMessage("Page phải lớn hơn hoặc bằng 1.");

            RuleFor(x => x.PageSize)
                .GreaterThanOrEqualTo(1)
                .WithMessage("PageSize phải lớn hơn hoặc bằng 1.")
                .LessThanOrEqualTo(100)
                .WithMessage("PageSize không được vượt quá 100.");
        }
    }
}
