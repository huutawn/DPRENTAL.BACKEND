namespace Dprental.InventoryService.Application.DTOs.Catalog;

public sealed record CatalogBatchOptionResponse(
    string Value,
    string Label);

public sealed record CatalogBatchFieldResponse(
    string Key,
    string Label,
    string DataType,
    bool Required,
    bool Filterable,
    bool Primary,
    int DisplayOrder,
    decimal? Min,
    decimal? Max,
    IReadOnlyList<CatalogBatchOptionResponse>? Options,
    string? Unit,
    string? Description);

public sealed record CatalogBatchSchemaItemResponse(
    int Version,
    IReadOnlyList<CatalogBatchFieldResponse> Fields);

public sealed record CatalogBatchSchemasResponse(
    Guid EquipmentTypeId,
    int CurrentSchemaVersion,
    IReadOnlyList<CatalogBatchSchemaItemResponse> Schemas);
