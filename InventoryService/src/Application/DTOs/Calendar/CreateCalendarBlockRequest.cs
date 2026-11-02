using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.Serialization.Converters;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Application.DTOs.Calendar;

/// <summary>
/// Yêu cầu tạo mới một khoảng chặn lịch cho tài sản.
/// </summary>
public sealed record CreateCalendarBlockRequest
{
    [JsonRequired]
    public DateOnly StartDate { get; init; }

    [JsonRequired]
    public DateOnly EndDate { get; init; }

    [JsonRequired]
    [JsonConverter(typeof(CalendarBlockReasonStrictJsonConverter))]
    public CalendarBlockReason Reason { get; init; }

    public string? Note { get; init; }
}
