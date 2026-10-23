using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.Kafka.Transport;

/// <summary>
/// Converter chuẩn hóa serialization DateTimeOffset thành định dạng UTC ISO-8601 kết thúc bằng 'Z'
/// (ví dụ: 2026-10-23T10:00:00.123456Z) phù hợp với Draft 2020-12 schema validator.
/// </summary>
public sealed class UtcIso8601DateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        var utc = value.ToUniversalTime();
        writer.WriteStringValue(utc.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture));
    }
}
