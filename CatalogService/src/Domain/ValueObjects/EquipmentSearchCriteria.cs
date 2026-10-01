namespace Dprental.CatalogService.Domain.ValueObjects;

/// <summary>Không có Status: repository luôn chỉ trả Published, client không override được.</summary>
public sealed record EquipmentSearchCriteria(
    string? Keyword,
    Guid? CategoryId,
    Guid? RegionId,
    decimal? MinPrice,
    decimal? MaxPrice,
    EquipmentSort Sort,
    int Page,
    int PageSize
    );