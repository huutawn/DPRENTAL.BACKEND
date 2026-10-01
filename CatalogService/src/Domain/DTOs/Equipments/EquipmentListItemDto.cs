namespace Dprental.CatalogService.Domain.DTOs.Equipments;

public sealed record EquipmentListItemDto(
    Guid Id,
    string Name,
    string? Brand,
    decimal DailyPrice,
    Guid RegionId,
    Guid? ThumbnailMediaId,
    CategoryDto Category);
