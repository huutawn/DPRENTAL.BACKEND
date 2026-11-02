
namespace Dprental.InventoryService.Application.DTOs.Warehouse;

/// <summary>
/// DTO phản hồi thông tin chi tiết của kho thuộc sở hữu người dùng.
/// </summary>
public sealed record WarehouseResponse(
    Guid OwnerUserId,
    string? AddressDetail,
    string? ProvinceCode,
    string? ProvinceName,
    string? WardCode,
    string? WardName,
    double? Latitude,
    double? Longitude,
    bool HasValidAddress,
    DateTimeOffset UpdatedAt,
    uint Version
);
