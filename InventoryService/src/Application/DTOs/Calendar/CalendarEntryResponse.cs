using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Calendar;

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
