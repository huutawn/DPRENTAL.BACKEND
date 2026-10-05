using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.CatalogService.Domain.Entities;

[JsonConverter(typeof(EquipmentTypeSpecDefinitionJsonConverter))]
public sealed record EquipmentTypeSpecDefinition
{
    private readonly IReadOnlyList<EquipmentTypeSpecField> fields = Array.Empty<EquipmentTypeSpecField>();

    [JsonPropertyName("fields")]
    public IReadOnlyList<EquipmentTypeSpecField> Fields
    {
        get => fields;
        init => fields = value != null ? value.Select(CloneField).ToArray() : Array.Empty<EquipmentTypeSpecField>();
    }

    private static EquipmentTypeSpecField CloneField(EquipmentTypeSpecField field)
    {
        if (field is null)
        {
            return null!;
        }

        return field with
        {
            Options = field.Options != null ? field.Options.Select(o => o is not null ? o with { } : null!).ToArray() : null,
            Metadata = field.Metadata.HasValue ? field.Metadata.Value.Clone() : null
        };
    }
}

public sealed record EquipmentTypeSpecField
{
    private readonly IReadOnlyList<EquipmentTypeSpecOption>? options;

    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("dataType")]
    public string DataType { get; init; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("filterable")]
    public bool Filterable { get; init; }

    [JsonPropertyName("primary")]
    public bool Primary { get; init; }

    [JsonPropertyName("displayOrder")]
    public int DisplayOrder { get; init; }

    [JsonPropertyName("min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Min { get; init; }

    [JsonPropertyName("max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Max { get; init; }

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<EquipmentTypeSpecOption>? Options
    {
        get => options;
        init => options = value != null ? value.Select(o => o is not null ? o with { } : null!).ToArray() : null;
    }

    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Metadata { get; init; }
}

public sealed record EquipmentTypeSpecOption
{
    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;
}
