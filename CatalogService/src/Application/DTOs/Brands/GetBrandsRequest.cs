using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.DTOs.Brands;

/// <summary>
/// Tham số truy vấn danh sách Brand công khai (chỉ active).
/// </summary>
public sealed record GetBrandsRequest
{
    public string? Keyword { get; init; }
    public int Page { get; init; } = PageRequest.DefaultPage;
    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
    public string? Sort { get; init; }
}
