using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Mapping;

/// <summary>
/// Chuyển đổi thực thể đơn vị thiết bị vật lý (InventoryAsset) sang DTO InventoryAssetResponse.
/// </summary>
public static class InventoryAssetMapper
{
    public static InventoryAssetResponse ToResponse(InventoryAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        return new InventoryAssetResponse(
            asset.Id,
            asset.EquipmentListingId,
            asset.AssetCode,
            asset.SerialNumber,
            asset.ManufactureYear,
            asset.AcceptingBookings,
            asset.SuspensionReason,
            asset.CreatedAt,
            asset.UpdatedAt);
    }
}
