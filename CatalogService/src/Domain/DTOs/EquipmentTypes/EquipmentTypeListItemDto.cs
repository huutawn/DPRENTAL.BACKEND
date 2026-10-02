namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

public sealed record EquipmentTypeListItemDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    IReadOnlyList<CategoryDto> Categories,
    Guid? ThumbnailMediaId,
    string? ThumbnailUrl);
