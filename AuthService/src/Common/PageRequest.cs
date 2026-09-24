namespace Dprental.AuthService.Common;

/// <summary>Yêu cầu phân trang dùng chung cho các API danh sách.</summary>
public sealed record PageRequest(int Page = PageRequest.DefaultPage, int PageSize = PageRequest.DefaultPageSize)
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>Kiểm tra tính hợp lệ của tham số phân trang: page &gt; 0, 1 &lt;= pageSize &lt;= 100.</summary>
    public Result Validate()
    {
        if (Page <= 0)
        {
            return Result.Failure(AuthErrors.Paging.InvalidPage);
        }

        if (PageSize is <= 0 or > MaxPageSize)
        {
            return Result.Failure(AuthErrors.Paging.InvalidPageSize);
        }

        return Result.Success();
    }
}
