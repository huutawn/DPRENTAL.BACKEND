namespace Dprental.CatalogService.Common;

/// <summary>Yêu cầu phân trang dùng chung cho các API danh sách.</summary>
public sealed record PageRequest(
    int Page = PageRequest.DefaultPage,
    int PageSize = PageRequest.DefaultPageSize,
    string? Search = null)
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int MaxSearchLength = 100;

    /// <summary>Chuẩn hóa từ khóa một lần trước khi tạo điều kiện tìm kiếm.</summary>
    public string? GetSearchTerm()
    {
        string? trimmed = Search?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToLowerInvariant();
    }

    /// <summary>Kiểm tra tính hợp lệ của tham số phân trang: page &gt; 0, 1 &lt;= pageSize &lt;= 100.</summary>
    public Result Validate()
    {
        if (Page <= 0)
        {
            return Result.Failure(CatalogErrors.Paging.InvalidPage);
        }

        if (PageSize is <= 0 or > MaxPageSize)
        {
            return Result.Failure(CatalogErrors.Paging.InvalidPageSize);
        }
        if ((long)(Page - 1) * PageSize > int.MaxValue)
        {
            return Result.Failure(CatalogErrors.Paging.Overflow);
        }


        if (Search?.Trim().Length > MaxSearchLength)
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidKeyword);
        }

        return Result.Success();
    }
}
