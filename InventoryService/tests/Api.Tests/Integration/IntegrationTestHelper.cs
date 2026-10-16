using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Api.Tests.Integration;

public static class IntegrationTestHelper
{
    public static async Task<InventoryAsset> InsertTestAssetAsync(
        InventoryServiceDbContext db,
        Guid? assetId = null,
        Guid? ownerId = null,
        Guid? eqTypeId = null,
        Guid? brandId = null,
        string? name = null,
        string? description = null,
        string model = "Sony FX3",
        int schemaVersion = 1,
        string specValues = "{}",
        decimal dailyRate = 100_000m,
        DateTimeOffset? now = null,
        bool acceptingBookings = true,
        string? serialNumber = null,
        int? manufactureYear = null,
        Guid? thumbnailMediaId = null,
        IReadOnlyList<Guid>? mediaIds = null)
    {
        var actualId = assetId ?? Guid.NewGuid();
        var actualNow = now ?? DateTimeOffset.UtcNow;
        var actualOwner = ownerId ?? Guid.NewGuid();
        var actualEqType = eqTypeId ?? Guid.NewGuid();
        var actualBrand = brandId ?? Guid.NewGuid();
        var actualName = string.IsNullOrWhiteSpace(name) ? model : name;
        var actualMediaIds = mediaIds ?? (thumbnailMediaId.HasValue ? [thumbnailMediaId.Value] : null);
        var listing = EquipmentListing.Create(
            Guid.NewGuid(),
            actualOwner,
            actualEqType,
            actualBrand,
            actualName,
            description,
            model,
            schemaVersion,
            specValues,
            dailyRate,
            null,
            null,
            "VND",
            actualNow,
            manufactureYear ?? 2024,
            actualMediaIds,
            thumbnailMediaId).Value;
        if (!await db.Warehouses.AnyAsync(w => w.OwnerUserId == actualOwner))
        {
            db.Warehouses.Add(Warehouse.CreateWithAddress(actualOwner, "123 Test Street", "01", "Thành phố Hà Nội", "00070", "Phường Hoàn Kiếm", 21.0, 105.8, actualNow).Value);
        }
        listing.UpdateStatus(EquipmentListingStatus.Active, true, actualNow);
        db.EquipmentListings.Add(listing);

        var actualSerial = string.IsNullOrWhiteSpace(serialNumber) ? $"SN-{actualId:N}" : serialNumber;
        var asset = InventoryAsset.Create(
            actualId,
            listing.Id,
            actualSerial,
            actualNow,
            manufactureYear).Value;

        if (!acceptingBookings)
        {
            asset.DisableBookings("Suspended for test", actualNow);
        }

        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        typeof(InventoryAsset).GetProperty(nameof(InventoryAsset.Listing))?.SetValue(asset, listing);
        return asset;
    }

    public static async Task<EquipmentListing> InsertTestListingAsync(
        InventoryServiceDbContext db,
        Guid? listingId = null,
        Guid? ownerId = null,
        Guid? eqTypeId = null,
        Guid? brandId = null,
        string? name = null,
        string? description = null,
        string model = "Sony FX3",
        int schemaVersion = 1,
        string specValues = "{}",
        decimal dailyRate = 100_000m,
        EquipmentListingStatus status = EquipmentListingStatus.Active,
        DateTimeOffset? now = null,
        int initialUnits = 1,
        Guid? thumbnailMediaId = null,
        int manufactureYear = 2024,
        IReadOnlyList<Guid>? mediaIds = null)
    {
        var actualListingId = listingId ?? Guid.NewGuid();
        var actualNow = now ?? DateTimeOffset.UtcNow;
        var actualOwner = ownerId ?? Guid.NewGuid();
        var actualEqType = eqTypeId ?? Guid.NewGuid();
        var actualBrand = brandId ?? Guid.NewGuid();
        var actualName = string.IsNullOrWhiteSpace(name) ? model : name;
        var actualMediaIds = mediaIds ?? (thumbnailMediaId.HasValue ? [thumbnailMediaId.Value] : null);
        var listing = EquipmentListing.Create(
            actualListingId,
            actualOwner,
            actualEqType,
            actualBrand,
            actualName,
            description,
            model,
            schemaVersion,
            specValues,
            dailyRate,
            null,
            null,
            "VND",
            actualNow,
            manufactureYear,
            actualMediaIds,
            thumbnailMediaId).Value;
        if (!await db.Warehouses.AnyAsync(w => w.OwnerUserId == actualOwner))
        {
            db.Warehouses.Add(Warehouse.CreateWithAddress(actualOwner, "123 Test Street", "01", "Thành phố Hà Nội", "00070", "Phường Hoàn Kiếm", 21.0, 105.8, actualNow).Value);
        }
        db.EquipmentListings.Add(listing);

        for (int i = 0; i < initialUnits; i++)
        {
            var uId = Guid.NewGuid();
            var asset = InventoryAsset.Create(uId, actualListingId, $"SN-{uId:N}", actualNow, manufactureYear).Value;
            db.Assets.Add(asset);
            listing.Assets.Add(asset);
        }

        if (status == EquipmentListingStatus.Active)
        {
            listing.UpdateStatus(EquipmentListingStatus.Active, initialUnits > 0, actualNow);
        }
        else if (status == EquipmentListingStatus.Inactive)
        {
            listing.UpdateStatus(EquipmentListingStatus.Inactive, true, actualNow);
        }

        await db.SaveChangesAsync();
        return listing;
    }
}
