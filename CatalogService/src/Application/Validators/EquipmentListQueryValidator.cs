using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.Equipments;
using Microsoft.Extensions.Options;

namespace Dprental.CatalogService.Application.Validators;

public sealed class EquipmentListQueryValidator(IOptions<CatalogDiscoveryOptions> options)
{
    private static readonly HashSet<string> AllowedSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        "newest",
        "price_asc",
        "price_desc"
    };

    public Result Validate(GetEquipmentsRequest request)
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

        // Kiểm tra tràn số nguyên khi tính toán offset phân trang: (Page - 1) * PageSize
        if ((long)(request.Page - 1) * request.PageSize > int.MaxValue)
        {
            return Result.Failure(CatalogErrors.Paging.Overflow);
        }

        int maxKeywordLength = options.Value.MaxKeywordLength;
        if (request.Keyword?.Trim().Length > maxKeywordLength)
        {
            return Result.Failure(CatalogErrors.Equipment.InvalidKeyword);
        }

        if (request.MinPrice < 0)
        {
            return Result.Failure(CatalogErrors.Equipment.InvalidMinPrice);
        }

        if (request.MaxPrice < 0)
        {
            return Result.Failure(CatalogErrors.Equipment.InvalidMaxPrice);
        }

        if (request.MinPrice is not null && request.MaxPrice is not null && request.MinPrice > request.MaxPrice)
        {
            return Result.Failure(CatalogErrors.Equipment.InvalidPriceRange);
        }

        if (!string.IsNullOrWhiteSpace(request.Sort) && !AllowedSorts.Contains(request.Sort.Trim()))
        {
            return Result.Failure(CatalogErrors.Equipment.InvalidSort);
        }

        return Result.Success();
    }
}
