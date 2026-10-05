using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Mapping;

/// <summary>
/// Chuyển đổi thực thể InventoryAsset sang DTO InventoryAssetResponse.
/// Không chứa logic validation hoặc persistence.
/// </summary>
public static class InventoryAssetMapper
{
    public static InventoryAssetResponse ToResponse(InventoryAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        using var doc = JsonDocument.Parse(asset.SpecValues);
        return new InventoryAssetResponse(
            asset.Id,
            asset.OwnerUserId,
            asset.EquipmentTypeId,
            asset.Model,
            asset.SerialNumber,
            asset.ManufactureYear,
            asset.ThumbnailMediaId,
            asset.SchemaVersion,
            doc.RootElement.Clone(),
            asset.AcceptingBookings,
            asset.SuspensionReason,
            asset.CreatedAt,
            asset.UpdatedAt);
    }
}
