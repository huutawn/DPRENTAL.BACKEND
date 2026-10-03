using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Pagination;

/// <summary>
/// Phản hồi danh sách phân trang theo chuẩn DPRENTAL.
/// </summary>
public sealed record PagedResponse<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("totalPages")] int TotalPages
);
