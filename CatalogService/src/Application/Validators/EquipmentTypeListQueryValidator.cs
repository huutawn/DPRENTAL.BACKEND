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

        Result pagingResult = PaginationValidationHelper.Validate(request.Page, request.PageSize);
        if (pagingResult.IsFailure)
        {
            return pagingResult;
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
