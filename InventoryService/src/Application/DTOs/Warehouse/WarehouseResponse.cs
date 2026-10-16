using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Warehouse;

/// <summary>
/// DTO phản hồi thông tin chi tiết của kho thuộc sở hữu người dùng.
/// </summary>
public sealed record WarehouseResponse(
    [property: JsonPropertyName("ownerUserId")] Guid OwnerUserId,
    [property: JsonPropertyName("addressDetail")] string? AddressDetail,
    [property: JsonPropertyName("provinceCode")] string? ProvinceCode,
    [property: JsonPropertyName("provinceName")] string? ProvinceName,
    [property: JsonPropertyName("wardCode")] string? WardCode,
    [property: JsonPropertyName("wardName")] string? WardName,
    [property: JsonPropertyName("latitude")] double? Latitude,
    [property: JsonPropertyName("longitude")] double? Longitude,
    [property: JsonPropertyName("hasValidAddress")] bool HasValidAddress,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("version")] uint Version
);
