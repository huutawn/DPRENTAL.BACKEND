namespace CatalogService.Application.DTOs.Category;

/// <summary>
/// Tham số truy vấn danh sách Category công khai (chỉ active).
/// </summary>
public sealed record GetCategoriesRequest
{
    public string? Keyword { get; init; }
    public string? Sort { get; init; }
    public string? Scope { get; init; }
    public string? ListingStatus { get; init; }
}
