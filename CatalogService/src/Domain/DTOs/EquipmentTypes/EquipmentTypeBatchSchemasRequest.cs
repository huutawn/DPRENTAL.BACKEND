namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

public sealed record EquipmentTypeBatchSchemasRequest(
    IReadOnlyList<int> Versions);
