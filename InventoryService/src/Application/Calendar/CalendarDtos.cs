using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Application.Calendar;

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

/// <summary>
/// Yêu cầu tạo mới một khoảng chặn lịch cho tài sản.
/// </summary>
public sealed record CreateCalendarBlockRequest
{
    [JsonRequired]
    [JsonPropertyName("startDate")]
    public DateOnly StartDate { get; init; }

    [JsonRequired]
    [JsonPropertyName("endDate")]
    public DateOnly EndDate { get; init; }

    [JsonRequired]
    [JsonPropertyName("reason")]
    [JsonConverter(typeof(CalendarBlockReasonStrictJsonConverter))]
    public CalendarBlockReason Reason { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary>
/// DTO phản hồi khi tạo khoảng chặn lịch thành công.
/// </summary>
public sealed record CalendarBlockResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("assetId")] Guid AssetId,
    [property: JsonPropertyName("startDate")] DateOnly StartDate,
    [property: JsonPropertyName("endDate")] DateOnly EndDate,
    [property: JsonPropertyName("reason")]
    [property: JsonConverter(typeof(JsonStringEnumConverter<CalendarBlockReason>))]
    CalendarBlockReason Reason,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt
);

/// <summary>
/// Entry lịch tổng hợp trong cửa sổ xem (CalendarBlock, Held hoặc Allocated).
/// Các trường không áp dụng được bỏ khỏi JSON (WhenWritingNull).
/// </summary>
public sealed record CalendarEntryResponse
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("startDate")]
    public required DateOnly StartDate { get; init; }

    [JsonPropertyName("endDate")]
    public required DateOnly EndDate { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    [JsonPropertyName("holdExpiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? HoldExpiresAt { get; init; }
}

/// <summary>
/// DTO phản hồi lịch tổng hợp của tài sản trong cửa sổ xem 6 tháng.
/// </summary>
public sealed record CalendarResponse
{
    [JsonPropertyName("assetId")]
    public required Guid AssetId { get; init; }

    [JsonPropertyName("startDate")]
    public required DateOnly StartDate { get; init; }

    [JsonPropertyName("endDate")]
    public required DateOnly EndDate { get; init; }

    [JsonPropertyName("entries")]
    public required IReadOnlyList<CalendarEntryResponse> Entries { get; init; }
}

/// <summary>
/// Payload sự kiện thay đổi lịch khả dụng phát ra Kafka qua Outbox (v1).
/// </summary>
public sealed record InventoryAssetAvailabilityChangedPayload(
    [property: JsonPropertyName("assetId")] Guid AssetId,
    [property: JsonPropertyName("changedAt")] DateTimeOffset ChangedAt
);
