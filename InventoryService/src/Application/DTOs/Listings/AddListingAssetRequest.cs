using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Yêu cầu thêm một đơn vị thiết bị vật lý vào một EquipmentListing hiện có bằng số serial (tùy chọn; cho phép null nếu thiết bị không có serial).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddListingAssetRequest(
    string? SerialNumber = null
);

/// <summary>
/// Yêu cầu thêm hàng loạt thiết bị vật lý cho một EquipmentListing trong một thao tác.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BatchAddListingAssetsRequest(
    IReadOnlyList<AddListingAssetRequest> Assets
);
