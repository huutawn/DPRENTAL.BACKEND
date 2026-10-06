using Dprental.CatalogService.Domain.Entities;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

public sealed record EquipmentTypePublishedSchemaResponse(
    Guid EquipmentTypeId,
    int Version,
    IReadOnlyList<EquipmentTypeSpecField> Fields);
