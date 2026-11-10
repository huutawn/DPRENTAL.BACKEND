
namespace Dprental.InventoryService.Application.DTOs.Pagination;

/// <summary>
/// Phản hồi danh sách phân trang theo chuẩn DPRENTAL.
/// </summary>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages
);
