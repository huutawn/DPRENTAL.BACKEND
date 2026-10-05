using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Availability;

/// <summary>
/// Danh sách các khoảng bận đã được làm sạch của tài sản theo DPR-150.
/// Chỉ chứa assetId, acceptingBookings và ranges (gồm startDate, endDate).
/// </summary>
public sealed record AssetBusyRangesResponse(
    [property: JsonPropertyName("assetId")] Guid AssetId,
    [property: JsonPropertyName("acceptingBookings")] bool AcceptingBookings,
    [property: JsonPropertyName("ranges")] IReadOnlyList<BusyRangeDto> Ranges
);
