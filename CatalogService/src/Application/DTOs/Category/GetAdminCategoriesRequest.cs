using Dprental.CatalogService.Common;

namespace CatalogService.Application.DTOs.Category;

/// <summary>
/// Tham số truy vấn danh sách Category cho Admin (hỗ trợ lọc isActive).
/// </summary>
public sealed record GetAdminCategoriesRequest
{
    public string? Keyword { get; init; }
    public int Page { get; init; } = PageRequest.DefaultPage;
    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
    public string? Sort { get; init; }
    public bool? IsActive { get; init; }
}
