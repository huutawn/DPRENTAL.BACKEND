namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

public sealed record EquipmentTypePrimarySpecDto(
    string Key,
    string Label,
    string? Unit);
