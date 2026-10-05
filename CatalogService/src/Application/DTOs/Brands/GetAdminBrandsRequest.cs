using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.DTOs.Brands;

/// <summary>
/// Tham số truy vấn danh sách Brand cho Admin (hỗ trợ lọc isActive).
/// </summary>
public sealed record GetAdminBrandsRequest
{
    public string? Keyword { get; init; }
    public int Page { get; init; } = PageRequest.DefaultPage;
    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
    public string? Sort { get; init; }
    public bool? IsActive { get; init; }
}
