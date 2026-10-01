using System.Text.Json.Serialization;

namespace Dprental.CatalogService.Common;

/// <summary>Kết quả danh sách phân trang chuẩn hoá { items, page, pageSize, totalCount, totalPages }.</summary>
public sealed record PagedResult<T>
{
    public PagedResult(IReadOnlyList<T> items, int page, int pageSize, int totalCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items;
        Page = page;
        PageSize = pageSize;
        TotalCount = totalCount;
        TotalPages = totalCount <= 0
            ? 0
            : (int)Math.Ceiling((double)totalCount / Math.Max(1, pageSize));
    }

    [JsonConstructor]
    public PagedResult(IReadOnlyList<T> items, int page, int pageSize, int totalCount, int totalPages)
    {
        Items = items ?? [];
        Page = page;
        PageSize = pageSize;
        TotalCount = totalCount;
        TotalPages = totalPages;
    }

    public IReadOnlyList<T> Items { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
}

/// <summary>Factory tạo <see cref="PagedResult{T}"/> tránh vi phạm CA1000.</summary>
public static class PagedResult
{
    public static PagedResult<T> Create<T>(IReadOnlyList<T> items, int page, int pageSize, int totalCount) =>
        new(items, page, pageSize, totalCount);
}
