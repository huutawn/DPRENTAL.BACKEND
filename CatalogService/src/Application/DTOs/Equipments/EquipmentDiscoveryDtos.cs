using System.Text.Json;

namespace Dprental.CatalogService.Application.DTOs;

/// <summary>Query string của GET /equipments. Page/PageSize nullable để phân biệt "không truyền" với "giá trị sai".</summary>
public sealed class EquipmentListQuery
{
    public string? Keyword { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? RegionId { get; init; }
    public decimal? MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }
    public string? Sort { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

public sealed record CategoryDto(Guid Id, string Name);

public sealed record EquipmentListItemDto(
    Guid Id, string Name, string? Brand, decimal DailyPrice,
    Guid RegionId, Guid? ThumbnailMediaId, CategoryDto Category);

public sealed record EquipmentDetailDto(
    Guid Id, string Code, string Name, string? Brand, string? Description,
    decimal DailyPrice, Guid RegionId, CategoryDto Category,
    JsonElement? Specifications, IReadOnlyList<Guid> MediaIds);

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items, int Page, int PageSize, int TotalItems, int TotalPages);

public sealed record ValidationError(string Field, string Message);