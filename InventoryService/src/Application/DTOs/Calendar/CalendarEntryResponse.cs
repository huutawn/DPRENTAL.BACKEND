using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Calendar;

/// <summary>
/// Entry lịch tổng hợp trong cửa sổ xem (CalendarBlock, Held hoặc Allocated).
/// Các trường không áp dụng được bỏ khỏi JSON (WhenWritingNull).
/// </summary>
public sealed record CalendarEntryResponse
{
    public required Guid Id { get; init; }

    public required string Type { get; init; }

    public required DateOnly StartDate { get; init; }

    public required DateOnly EndDate { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? HoldExpiresAt { get; init; }
}
