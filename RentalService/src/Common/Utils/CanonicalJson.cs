using System.Text;
using System.Text.Json;

namespace Dprental.RentalService.Common.Utils;

public static class CanonicalJson
{
    public static string Normalize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        using var doc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteElement(doc.RootElement, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static void WriteElement(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // Deterministic property order by Ordinal
                var properties = element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToList();

                foreach (var prop in properties)
                {
                    writer.WritePropertyName(prop.Name);
                    WriteElement(prop.Value, writer);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                // Array order MUST be strictly preserved
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(item, writer);
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out long l))
                {
                    writer.WriteNumberValue(l);
                }
                else if (element.TryGetDecimal(out decimal d))
                {
                    writer.WriteNumberValue(d);
                }
                else if (element.TryGetDouble(out double dbl))
                {
                    writer.WriteNumberValue(dbl);
                }
                else
                {
                    writer.WriteRawValue(element.GetRawText());
                }
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                writer.WriteRawValue(element.GetRawText());
                break;
        }
    }

    public static bool AreEquivalent(string? json1, string? json2)
    {
        if (string.Equals(json1, json2, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(json1) || string.IsNullOrWhiteSpace(json2))
        {
            return false;
        }

        try
        {
            return string.Equals(Normalize(json1), Normalize(json2), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
