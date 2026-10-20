using CatalogService.Application.DTOs.Category;
using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Common;
using Microsoft.Extensions.Options;

namespace Dprental.CatalogService.Application.Validators;

public sealed class CategoryListQueryValidator(IOptions<CatalogDiscoveryOptions> options)
{
    private static readonly HashSet<string> AllowedSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        "newest",
        "name_asc",
        "name_desc"
    };


    public Result ValidateDiscovery(GetCategoriesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Keyword != null && (request.Keyword.Contains('\0') || request.Keyword.Trim().Length > options.Value.MaxKeywordLength))
        {
            return Result.Failure(CatalogErrors.Category.InvalidKeyword);
        }

        Result sortResult = DiscoveryQueryValidationHelper.ValidateDiscoverySort(request.Sort, CatalogErrors.Category.InvalidSort);
        if (sortResult.IsFailure)
        {
            return sortResult;
        }

        return DiscoveryQueryValidationHelper.ValidateScopeAndListingStatus(request.Scope, request.ListingStatus);
    }
    public Result Validate(int page, int pageSize, string? keyword, string? sort)
    {
        Result pagingResult = PaginationValidationHelper.Validate(page, pageSize);
        if (pagingResult.IsFailure)
        {
            return pagingResult;
        }

        if (keyword != null && (keyword.Contains('\0') || keyword.Trim().Length > options.Value.MaxKeywordLength))
        {
            return Result.Failure(CatalogErrors.Category.InvalidKeyword);
        }

        if (!string.IsNullOrWhiteSpace(sort) && !AllowedSorts.Contains(sort.Trim()))
        {
            return Result.Failure(CatalogErrors.Category.InvalidSort);
        }

        return Result.Success();
    }
}
