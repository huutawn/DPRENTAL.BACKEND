using Dprental.CatalogService.Domain.Entities;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

public sealed record EquipmentTypeBatchSchemaItemDto(
    int Version,
    IReadOnlyList<EquipmentTypeSpecField> Fields);

public sealed record EquipmentTypeBatchSchemasResponse(
    Guid EquipmentTypeId,
    int CurrentSchemaVersion,
    IReadOnlyList<EquipmentTypeBatchSchemaItemDto> Schemas);
