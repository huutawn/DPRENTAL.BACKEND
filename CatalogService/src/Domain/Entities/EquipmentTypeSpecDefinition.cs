using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.CatalogService.Domain.Entities;

[JsonConverter(typeof(EquipmentTypeSpecDefinitionJsonConverter))]
public sealed record EquipmentTypeSpecDefinition
{
    private readonly IReadOnlyList<EquipmentTypeSpecField> fields = Array.Empty<EquipmentTypeSpecField>();

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

    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string DataType { get; init; } = string.Empty;

    public bool Required { get; init; }

    public bool Filterable { get; init; }

    public bool Primary { get; init; }

    public int DisplayOrder { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Min { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Max { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<EquipmentTypeSpecOption>? Options
    {
        get => options;
        init => options = value != null ? value.Select(o => o is not null ? o with { } : null!).ToArray() : null;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Metadata { get; init; }
}

public sealed record EquipmentTypeSpecOption
{
    public string Value { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;
}
