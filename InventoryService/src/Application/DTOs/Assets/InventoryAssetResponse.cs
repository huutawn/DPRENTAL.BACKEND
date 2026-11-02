
namespace Dprental.InventoryService.Application.DTOs.Assets;

/// <summary>
/// Thông tin phản hồi chi tiết của đơn vị thiết bị vật lý trong kho.
/// </summary>
public sealed record InventoryAssetResponse(
    Guid Id,
    Guid EquipmentListingId,
    string AssetCode,
    string? SerialNumber,
    int? ManufactureYear,
    bool AcceptingBookings,
    string? SuspensionReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);
