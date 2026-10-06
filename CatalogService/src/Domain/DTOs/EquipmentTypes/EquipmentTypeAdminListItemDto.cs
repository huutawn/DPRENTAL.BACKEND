using Dprental.CatalogService.Common.Enums;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

/// <summary>Dòng trong danh sách quản trị: như danh sách công khai, kèm trạng thái và mốc thời gian vòng đời.</summary>
public sealed record EquipmentTypeAdminListItemDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    EquipmentTypeStatus Status,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime? PublishedAt,
    IReadOnlyList<CategoryDto> Categories,
    Guid? ThumbnailMediaId,
    string? ThumbnailUrl,
    int? CurrentSchemaVersion = null);
