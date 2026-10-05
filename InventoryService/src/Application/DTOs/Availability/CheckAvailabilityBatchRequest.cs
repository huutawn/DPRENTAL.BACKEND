using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Availability;

/// <summary>
/// Yêu cầu kiểm tra tính khả dụng hàng loạt tài sản trong khoảng thời gian đề xuất.
/// Giới hạn tối đa 500 ID đầu vào trước khi gộp trùng.
/// Sử dụng nullable dates để phân biệt missing/null với giá trị mặc định.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CheckAvailabilityBatchRequest(
    [property: JsonPropertyName("assetIds")] IReadOnlyList<Guid>? AssetIds,
    [property: JsonPropertyName("startDate")] DateOnly? StartDate,
    [property: JsonPropertyName("endDate")] DateOnly? EndDate
);
