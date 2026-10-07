using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

public sealed record EquipmentTypePublishedSchemaResponse(
    Guid EquipmentTypeId,
    [property: JsonPropertyName("schemaVersion")] int Version,
    IReadOnlyList<EquipmentTypePublishedSchemaField> Fields);

/// <summary>Public schema field shape used by frontend dynamic forms.</summary>
public sealed record EquipmentTypePublishedSchemaField(
    string Key,
    string Label,
    string DataType,
    bool Required,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Min,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Max,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Options,
    bool Filterable,
    bool Primary,
    int DisplayOrder,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Metadata);
