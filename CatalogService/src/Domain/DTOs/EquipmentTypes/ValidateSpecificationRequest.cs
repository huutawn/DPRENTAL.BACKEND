using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

[JsonConverter(typeof(ValidateSpecificationRequestJsonConverter))]
public sealed record ValidateSpecificationRequest
{
    public int SchemaVersion { get; init; }
    public JsonElement SpecValues { get; init; }
}

public sealed class ValidateSpecificationRequestJsonConverter : JsonConverter<ValidateSpecificationRequest>
{
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "schemaVersion",
        "specValues"
    };

    public override ValidateSpecificationRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Request body phải là JSON object.");
        }

        var rootMap = new Dictionary<string, JsonProperty>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!rootMap.TryAdd(prop.Name, prop))
            {
                throw new JsonException($"Thuộc tính '{prop.Name}' bị khai báo trùng lặp ở root envelope.");
            }
        }

        foreach (var propName in rootMap.Keys)
        {
            if (!AllowedProperties.Contains(propName))
            {
                throw new JsonException($"Thuộc tính envelope '{propName}' không hợp lệ.");
            }
        }

        if (!rootMap.TryGetValue("schemaVersion", out var versionProp))
        {
            throw new JsonException("Thiếu thuộc tính bắt buộc 'schemaVersion'.");
        }

        if (!rootMap.TryGetValue("specValues", out var valuesProp))
        {
            throw new JsonException("Thiếu thuộc tính bắt buộc 'specValues'.");
        }

        if (versionProp.Value.ValueKind != JsonValueKind.Number
            || !versionProp.Value.TryGetInt32(out int schemaVersion)
            || schemaVersion <= 0)
        {
            throw new JsonException("Thuộc tính 'schemaVersion' bắt buộc là số nguyên dương.");
        }

        if (valuesProp.Value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Thuộc tính 'specValues' phải là JSON object.");
        }

        var valuesMap = new Dictionary<string, JsonProperty>(StringComparer.Ordinal);
        foreach (var prop in valuesProp.Value.EnumerateObject())
        {
            if (!valuesMap.TryAdd(prop.Name, prop))
            {
                throw new JsonException($"Thuộc tính '{prop.Name}' bị trùng lặp trong 'specValues'.");
            }
        }

        return new ValidateSpecificationRequest
        {
            SchemaVersion = schemaVersion,
            SpecValues = valuesProp.Value.Clone()
        };
    }

    public override void Write(Utf8JsonWriter writer, ValidateSpecificationRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", value.SchemaVersion);
        writer.WritePropertyName("specValues");
        value.SpecValues.WriteTo(writer);
        writer.WriteEndObject();
    }
}
