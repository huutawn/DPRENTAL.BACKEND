using System.Text.Json.Serialization;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Application.DTOs.Calendar;

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
