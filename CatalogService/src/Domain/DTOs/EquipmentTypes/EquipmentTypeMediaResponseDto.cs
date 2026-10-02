namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

/// <summary>
/// Phần tử media trong response chi tiết equipment type.
/// </summary>
public sealed record EquipmentTypeMediaResponseDto(
    Guid MediaId,
    string? Url,
    int DisplayOrder,
    bool IsThumbnail);
