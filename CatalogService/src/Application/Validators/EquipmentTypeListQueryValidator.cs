using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
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

        return Validate(request.Page, request.PageSize, request.Keyword, request.Sort);
    }

    /// <summary>Kiểm tra truy vấn danh sách quản trị và trả về tập trạng thái cần lọc (rỗng nghĩa là mọi trạng thái).</summary>
    public Result<IReadOnlyList<EquipmentTypeStatus>> Validate(GetAdminEquipmentTypesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result commonResult = Validate(request.Page, request.PageSize, request.Keyword, request.Sort);
        if (commonResult.IsFailure)
        {
            return commonResult.Error;
        }

        HashSet<EquipmentTypeStatus> statuses = [];
        foreach (string? value in request.Status ?? [])
        {
            // Enum.TryParse nhận cả tên lẫn số, nên cần IsDefined để loại số ngoài enum (vd "7").
            if (!Enum.TryParse(value?.Trim(), ignoreCase: true, out EquipmentTypeStatus status) || !Enum.IsDefined(status))
            {
                return CatalogErrors.EquipmentType.InvalidStatus;
            }

            statuses.Add(status);
        }

        return statuses.ToList();
    }

    private Result Validate(int page, int pageSize, string? keyword, string? sort)
    {
        Result pagingResult = PaginationValidationHelper.Validate(page, pageSize);
        if (pagingResult.IsFailure)
        {
            return pagingResult;
        }

        int maxKeywordLength = options.Value.MaxKeywordLength;
        if (keyword?.Trim().Length > maxKeywordLength)
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidKeyword);
        }

        if (!string.IsNullOrWhiteSpace(sort) && !AllowedSorts.Contains(sort.Trim()))
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidSort);
        }

        return Result.Success();
    }
}
