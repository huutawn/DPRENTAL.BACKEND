using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Domain.IRepositories;

/// <summary>
/// Tham số tìm kiếm và phân trang Brand tại Repository layer.
/// </summary>
public sealed record BrandSearchQuery
{
    public string? Keyword { get; init; }
    public string? NameKeyword { get; init; }
    public bool? IsActive { get; init; }
    public int Page { get; init; } = PageRequest.DefaultPage;
    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
    public string? Sort { get; init; }
}
