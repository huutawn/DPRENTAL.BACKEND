namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

public sealed record CategoryDto(
    Guid Id,
    string Name,
    Guid? ImageMediaId = null,
    string? ImageUrl = null);
