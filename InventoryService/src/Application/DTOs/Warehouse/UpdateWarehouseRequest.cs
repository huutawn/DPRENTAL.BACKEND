using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Warehouse;

/// <summary>
/// Yêu cầu tạo mới hoặc cập nhật địa chỉ kho đầy đủ.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateWarehouseRequest(
    [property: JsonPropertyName("addressDetail")] string AddressDetail,
    [property: JsonPropertyName("provinceCode")] string ProvinceCode,
    [property: JsonPropertyName("wardCode")] string WardCode,
    [property: JsonPropertyName("latitude"), JsonRequired] double? Latitude,
    [property: JsonPropertyName("longitude"), JsonRequired] double? Longitude,
    [property: JsonPropertyName("expectedVersion")] uint? ExpectedVersion = null
);
