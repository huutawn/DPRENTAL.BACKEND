using Dprental.CatalogService.Domain.Entities;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

public sealed record EquipmentTypeSpecSchemaDto(
    Guid Id,
    Guid EquipmentTypeId,
    int Version,
    string Status,
    DateTime CreatedAt,
    DateTime? PublishedAt,
    EquipmentTypeSpecDefinition Definition);
