using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Application.Serialization.Converters;

/// <summary>
/// Converter tùy chỉnh cho CalendarBlockReason: bắt buộc kiểu chuỗi và cấm nhận số.
/// </summary>
public sealed class CalendarBlockReasonStrictJsonConverter : JsonConverter<CalendarBlockReason>
{
    private static readonly Dictionary<string, CalendarBlockReason> ValidReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(CalendarBlockReason.Maintenance)] = CalendarBlockReason.Maintenance,
        [nameof(CalendarBlockReason.OwnerReserved)] = CalendarBlockReason.OwnerReserved,
        [nameof(CalendarBlockReason.Inspection)] = CalendarBlockReason.Inspection,
        [nameof(CalendarBlockReason.Other)] = CalendarBlockReason.Other,
    };

    public override CalendarBlockReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Reason bắt buộc là chuỗi tên enum (Maintenance, OwnerReserved, Inspection, Other); không nhận số.");
        }

        string? raw = reader.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new JsonException("Reason không được để trống.");
        }

        string trimmed = raw.Trim();
        if (ValidReasons.TryGetValue(trimmed, out var reason))
        {
            return reason;
        }

        throw new JsonException($"Reason không hợp lệ: '{raw}'. Chỉ chấp nhận: Maintenance, OwnerReserved, Inspection, Other.");
    }

    public override void Write(Utf8JsonWriter writer, CalendarBlockReason value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
