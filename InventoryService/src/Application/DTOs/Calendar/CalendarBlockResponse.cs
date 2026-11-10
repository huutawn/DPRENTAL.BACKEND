using System.Text.Json.Serialization;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Application.DTOs.Calendar;

/// <summary>
/// DTO phản hồi khi tạo khoảng chặn lịch thành công.
/// </summary>
public sealed record CalendarBlockResponse(
    Guid Id,
    Guid AssetId,
    DateOnly StartDate,
    DateOnly EndDate,
    [property: JsonConverter(typeof(JsonStringEnumConverter<CalendarBlockReason>))]
    CalendarBlockReason Reason,
    string? Note,
    DateTimeOffset CreatedAt
);
