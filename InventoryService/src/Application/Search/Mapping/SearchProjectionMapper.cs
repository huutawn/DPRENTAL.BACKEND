using Dprental.InventoryService.Application.Search.Models;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Search.Mapping;

/// <summary>
/// Bộ chuyển đổi (mapper) dùng chung duy nhất cho việc chiếu (projection) Entity sang Search Documents.
/// Dùng chung cho cả SearchProjectionWorker và MeilisearchBackfillService.
/// </summary>
public static class SearchProjectionMapper
{
    public static PublicListingIndexDocument ToPublicListingDocument(EquipmentListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        return new PublicListingIndexDocument
        {
            Id = listing.Id.ToString("D"),
            Name = listing.Name,
            Model = listing.Model,
            Description = listing.Description,
            EquipmentTypeId = listing.EquipmentTypeId.ToString("D"),
            BrandId = listing.BrandId.ToString("D"),
            CreatedAt = listing.CreatedAt.ToUnixTimeMilliseconds(),
            UpdatedAt = listing.UpdatedAt.ToUnixTimeMilliseconds()
        };
    }

    public static OwnerListingIndexDocument ToOwnerListingDocument(EquipmentListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        return new OwnerListingIndexDocument
        {
            Id = listing.Id.ToString("D"),
            OwnerUserId = listing.OwnerUserId.ToString("D"),
            Name = listing.Name,
            Model = listing.Model,
            Description = listing.Description,
            EquipmentTypeId = listing.EquipmentTypeId.ToString("D"),
            BrandId = listing.BrandId.ToString("D"),
            Status = (int)listing.Status,
            CreatedAt = listing.CreatedAt.ToUnixTimeMilliseconds(),
            UpdatedAt = listing.UpdatedAt.ToUnixTimeMilliseconds()
        };
    }

    public static OwnerAssetIndexDocument ToOwnerAssetDocument(InventoryAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(asset.Listing, nameof(asset.Listing));

        return new OwnerAssetIndexDocument
        {
            Id = asset.Id.ToString("D"),
            OwnerUserId = asset.Listing.OwnerUserId.ToString("D"),
            EquipmentListingId = asset.EquipmentListingId.ToString("D"),
            SerialNumber = asset.SerialNumber,
            Model = asset.Listing.Model,
            EquipmentTypeId = asset.Listing.EquipmentTypeId.ToString("D"),
            BrandId = asset.Listing.BrandId.ToString("D"),
            ManufactureYear = asset.ManufactureYear,
            AcceptingBookings = asset.AcceptingBookings,
            CreatedAt = asset.CreatedAt.ToUnixTimeMilliseconds(),
            UpdatedAt = asset.UpdatedAt.ToUnixTimeMilliseconds()
        };
    }
}
