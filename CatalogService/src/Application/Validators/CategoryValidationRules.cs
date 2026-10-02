using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.Validators;

public static class CategoryValidationRules
{
    public const int MaxNameLength = 150;
    public const int MaxDescriptionLength = 1000;

    public static Result ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
        {
            return Result.Failure(CatalogErrors.Category.InvalidName);
        }

        return Result.Success();
    }

    public static Result ValidateDescription(string? description)
    {
        if (description != null && description.Length > MaxDescriptionLength)
        {
            return Result.Failure(CatalogErrors.Category.InvalidDescription);
        }

        return Result.Success();
    }
}
