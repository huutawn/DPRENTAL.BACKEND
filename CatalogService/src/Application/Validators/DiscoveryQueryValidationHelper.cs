using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.Validators;

public static class DiscoveryQueryValidationHelper
{
    public const string DefaultSort = "listing_count_desc";

    public static readonly HashSet<string> AllowedDiscoverySorts = new(StringComparer.OrdinalIgnoreCase)
    {
        "listing_count_desc",
        "listing_count_asc",
        "newest",
        "name_asc",
        "name_desc"
    };

    public static readonly HashSet<string> AllowedListingStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Draft",
        "Active",
        "Inactive"
    };

    public static Result ValidateScopeAndListingStatus(string? scope, string? listingStatus)
    {
        string normalizedScope = string.IsNullOrWhiteSpace(scope) ? "public" : scope.Trim().ToLowerInvariant();

        if (normalizedScope is not ("public" or "mine"))
        {
            return Result.Failure(Error.Validation("INVALID_QUERY_PARAMETER", "scope chỉ nhận: public, mine."));
        }

        if (normalizedScope == "public" && listingStatus is not null)
        {
            return Result.Failure(Error.Validation("INVALID_QUERY_PARAMETER", "listingStatus chỉ được phép dùng với scope='mine'."));
        }

        if (normalizedScope == "mine" && listingStatus is not null)
        {
            if (!AllowedListingStatuses.Contains(listingStatus.Trim()))
            {
                return Result.Failure(Error.Validation("INVALID_QUERY_PARAMETER", "listingStatus không hợp lệ. Chỉ chấp nhận: Draft, Active, Inactive."));
            }
        }

        return Result.Success();
    }

    public static Result ValidateDiscoverySort(string? sort, Error invalidSortError)
    {
        if (!string.IsNullOrWhiteSpace(sort) && !AllowedDiscoverySorts.Contains(sort.Trim()))
        {
            return Result.Failure(invalidSortError);
        }

        return Result.Success();
    }
}
