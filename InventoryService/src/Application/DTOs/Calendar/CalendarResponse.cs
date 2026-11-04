
namespace Dprental.InventoryService.Application.DTOs.Calendar;

/// <summary>
/// DTO phản hồi lịch tổng hợp của tài sản trong cửa sổ xem 6 tháng.
/// </summary>
public sealed record CalendarResponse
{
    public required Guid AssetId { get; init; }

    public required DateOnly StartDate { get; init; }

    public required DateOnly EndDate { get; init; }

    public required IReadOnlyList<CalendarEntryResponse> Entries { get; init; }
}
