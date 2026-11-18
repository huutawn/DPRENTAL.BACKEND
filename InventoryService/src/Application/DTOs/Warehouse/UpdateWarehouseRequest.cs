using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Warehouse;

/// <summary>
/// Yêu cầu tạo mới hoặc cập nhật địa chỉ kho đầy đủ.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateWarehouseRequest(
    string AddressDetail,
    string ProvinceCode,
    string WardCode,
    double? Latitude = null,
    double? Longitude = null,
    uint? ExpectedVersion = null,
    bool InitializeOnly = false
);
