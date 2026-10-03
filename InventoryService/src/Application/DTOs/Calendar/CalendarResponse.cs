using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Calendar;

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
