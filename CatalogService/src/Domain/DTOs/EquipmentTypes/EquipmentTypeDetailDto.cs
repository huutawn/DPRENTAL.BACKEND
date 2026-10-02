using Dprental.CatalogService.Common.Enums;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

public sealed record EquipmentTypeDetailDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    EquipmentTypeStatus Status,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime? PublishedAt,
    CategoryDto Category,
    IReadOnlyList<Guid> MediaIds,
    Guid? ThumbnailMediaId);
