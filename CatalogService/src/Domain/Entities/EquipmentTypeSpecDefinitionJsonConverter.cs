using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.CatalogService.Domain.Validation;

namespace Dprental.CatalogService.Domain.Entities;

public sealed class EquipmentTypeSpecDefinitionJsonConverter : JsonConverter<EquipmentTypeSpecDefinition>
{
    public override EquipmentTypeSpecDefinition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var result = SpecSchemaDefinitionValidator.Validate(doc.RootElement);
        if (result.IsFailure)
        {
            throw new JsonException(result.Error.Message);
        }

        return result.Value;
    }

    public override void Write(Utf8JsonWriter writer, EquipmentTypeSpecDefinition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("fields");
        writer.WriteStartArray();
        foreach (var field in value.Fields)
        {
            writer.WriteStartObject();
            writer.WriteString("key", field.Key);
            writer.WriteString("label", field.Label);
            writer.WriteString("dataType", field.DataType);
            writer.WriteBoolean("required", field.Required);
            writer.WriteBoolean("filterable", field.Filterable);
            writer.WriteBoolean("primary", field.Primary);
            writer.WriteNumber("displayOrder", field.DisplayOrder);

            if (field.Min.HasValue)
            {
                writer.WriteNumber("min", field.Min.Value);
            }

            if (field.Max.HasValue)
            {
                writer.WriteNumber("max", field.Max.Value);
            }

            if (field.Options != null)
            {
                writer.WritePropertyName("options");
                writer.WriteStartArray();
                foreach (var opt in field.Options)
                {
                    writer.WriteStartObject();
                    writer.WriteString("value", opt.Value);
                    writer.WriteString("label", opt.Label);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            if (field.Unit != null)
            {
                writer.WriteString("unit", field.Unit);
            }

            if (field.Description != null)
            {
                writer.WriteString("description", field.Description);
            }

            if (field.Metadata.HasValue)
            {
                writer.WritePropertyName("metadata");
                field.Metadata.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
