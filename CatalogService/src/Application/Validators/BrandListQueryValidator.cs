using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Common;
using Microsoft.Extensions.Options;

namespace Dprental.CatalogService.Application.Validators;

using Dprental.CatalogService.Application.DTOs.Brands;
public sealed class BrandListQueryValidator(IOptions<CatalogDiscoveryOptions> options)
{
    private static readonly HashSet<string> AllowedSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        "newest",
        "name_asc",
        "name_desc"
    };


    public Result ValidateDiscovery(GetBrandsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result pagingResult = PaginationValidationHelper.Validate(request.Page, request.PageSize);
        if (pagingResult.IsFailure)
        {
            return pagingResult;
        }

        if (request.Keyword != null && (request.Keyword.Contains('\0') || request.Keyword.Trim().Length > options.Value.MaxKeywordLength))
        {
            return Result.Failure(CatalogErrors.Brand.InvalidKeyword);
        }

        Result sortResult = DiscoveryQueryValidationHelper.ValidateDiscoverySort(request.Sort, CatalogErrors.Brand.InvalidSort);
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
            return Result.Failure(CatalogErrors.Brand.InvalidKeyword);
        }

        if (!string.IsNullOrWhiteSpace(sort) && !AllowedSorts.Contains(sort.Trim()))
        {
            return Result.Failure(CatalogErrors.Brand.InvalidSort);
        }

        return Result.Success();
    }
}
