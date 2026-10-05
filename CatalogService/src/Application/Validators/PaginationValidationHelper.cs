using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.Validators;

/// <summary>
/// Quy tắc kiểm tra phân trang dùng chung cho các API danh sách trong Catalog.
/// </summary>
public static class PaginationValidationHelper
{
    public static Result Validate(int page, int pageSize)
    {
        if (page <= 0)
        {
            return Result.Failure(CatalogErrors.Paging.InvalidPage);
        }

        if (pageSize is <= 0 or > PageRequest.MaxPageSize)
        {
            return Result.Failure(CatalogErrors.Paging.InvalidPageSize);
        }

        if ((long)(page - 1) * pageSize > int.MaxValue)
        {
            return Result.Failure(CatalogErrors.Paging.Overflow);
        }

        return Result.Success();
    }
}
