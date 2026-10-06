using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Domain.IRepositories;

/// <summary>
/// Tham số tìm kiếm và phân trang Category tại Repository layer.
/// </summary>
public sealed record CategorySearchQuery
{
    public string? Keyword { get; init; }
    public bool? IsActive { get; init; }
    public int Page { get; init; } = PageRequest.DefaultPage;
    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
    public string? Sort { get; init; }
}
