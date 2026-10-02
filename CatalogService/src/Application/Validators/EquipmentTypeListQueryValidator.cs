using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Microsoft.Extensions.Options;

namespace Dprental.CatalogService.Application.Validators;

public sealed class EquipmentTypeListQueryValidator(IOptions<CatalogDiscoveryOptions> options)
{
    private static readonly HashSet<string> AllowedSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        "newest",
        "name_asc",
        "name_desc"
    };

    public Result Validate(GetEquipmentTypesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Page <= 0)
        {
            return Result.Failure(CatalogErrors.Paging.InvalidPage);
        }

        if (request.PageSize is <= 0 or > PageRequest.MaxPageSize)
        {
            return Result.Failure(CatalogErrors.Paging.InvalidPageSize);
        }

        if ((long)(request.Page - 1) * request.PageSize > int.MaxValue)
        {
            return Result.Failure(CatalogErrors.Paging.Overflow);
        }

        int maxKeywordLength = options.Value.MaxKeywordLength;
        if (request.Keyword?.Trim().Length > maxKeywordLength)
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidKeyword);
        }

        if (!string.IsNullOrWhiteSpace(request.Sort) && !AllowedSorts.Contains(request.Sort.Trim()))
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidSort);
        }

        return Result.Success();
    }
}
