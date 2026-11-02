using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.DTOs.Availability;

namespace Dprental.InventoryService.Application.DTOs.Availability;

/// <summary>
/// Danh sách các khoảng bận đã được làm sạch của tài sản theo DPR-150.
/// Chỉ chứa assetId, acceptingBookings và ranges (gồm startDate, endDate).
/// </summary>
public sealed record AssetBusyRangesResponse(
    Guid AssetId,
    bool AcceptingBookings,
    IReadOnlyList<BusyRangeDto> Ranges
);
