using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Yêu cầu thêm một đơn vị thiết bị vật lý vào một EquipmentListing hiện có bằng số serial.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddListingAssetRequest(
    [property: JsonPropertyName("serialNumber")] string SerialNumber
);
