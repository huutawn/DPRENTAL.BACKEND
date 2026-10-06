using System.Text.Json;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

public sealed class InventoryAssetDomainTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid TestOwnerId = Guid.NewGuid();
    private static readonly Guid TestEquipmentTypeId = Guid.NewGuid();
    private static readonly Guid TestBrandId = Guid.NewGuid();

    private static AssetRentalRate CreateValidRate(Guid assetId) =>
        AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", FixedNow).Value;

    [Fact]
    public void Create_ValidMinimalFields_DefaultsAcceptingBookingsAndNullReason()
    {
        var id = Guid.NewGuid();
        var result = InventoryAsset.Create(
            id,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "Sony FX3 Cinema Line",
            1,
            "{\"resolution\":\"4K\"}",
            FixedNow,
            CreateValidRate(id));

        Assert.True(result.IsSuccess);
        var asset = result.Value;
        Assert.Equal(id, asset.Id);
        Assert.Equal(TestOwnerId, asset.OwnerUserId);
        Assert.Equal(TestEquipmentTypeId, asset.EquipmentTypeId);
        Assert.Equal(TestBrandId, asset.BrandId);
        Assert.Equal("Sony FX3 Cinema Line", asset.Model);
        Assert.Null(asset.SerialNumber);
        Assert.Null(asset.ManufactureYear);
        Assert.Null(asset.ThumbnailMediaId);
        Assert.Equal(1, asset.SchemaVersion);
        Assert.Equal("{\"resolution\":\"4K\"}", asset.SpecValues);
        Assert.True(asset.AcceptingBookings);
        Assert.Null(asset.SuspensionReason);
        Assert.Equal(FixedNow, asset.CreatedAt);
        Assert.Equal(FixedNow, asset.UpdatedAt);
    }

    [Fact]
    public void Create_ValidAllFields_NormalizesAndPersistsProperly()
    {
        var id = Guid.NewGuid();
        var thumbId = Guid.NewGuid();
        var result = InventoryAsset.Create(
            id,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "  Sony FX3  ",
            2,
            "{}",
            FixedNow,
            CreateValidRate(id),
            serialNumber: "  SN-123456  ",
            manufactureYear: 2024,
            thumbnailMediaId: thumbId);

        Assert.True(result.IsSuccess);
        var asset = result.Value;
        Assert.Equal("Sony FX3", asset.Model);
        Assert.Equal("SN-123456", asset.SerialNumber);
        Assert.Equal(2024, asset.ManufactureYear);
        Assert.Equal(thumbId, asset.ThumbnailMediaId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_InvalidModel_Fails(string? model)
    {
        var id = Guid.NewGuid();
        var result = InventoryAsset.Create(
            id, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            model!, 1, "{}", FixedNow, CreateValidRate(id));
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_MODEL", result.Error.Code);
    }

    [Fact]
    public void Create_ModelTooLong_Fails()
    {
        var longModel = new string('A', 201);
        var id = Guid.NewGuid();
        var result = InventoryAsset.Create(
            id, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            longModel, 1, "{}", FixedNow, CreateValidRate(id));
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_MODEL", result.Error.Code);
    }

    [Fact]
    public void Create_WhitespaceSerialNumber_NormalizedToNull()
    {
        var id = Guid.NewGuid();
        var result = InventoryAsset.Create(
            id, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Model X", 1, "{}", FixedNow,
            CreateValidRate(id),
            serialNumber: "   ");
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.SerialNumber);
    }

    [Fact]
    public void Create_SerialNumberTooLong_Fails()
    {
        var longSerial = new string('S', 101);
        var id = Guid.NewGuid();
        var result = InventoryAsset.Create(
            id, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Model X", 1, "{}", FixedNow,
            CreateValidRate(id),
            serialNumber: longSerial);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_SERIAL_NUMBER", result.Error.Code);
    }

    [Theory]
    [InlineData(1899)]
    [InlineData(2030)] // Greater than 2026 (Vietnam current year in FixedNow)
    public void Create_ManufactureYearOutOfRange_Fails(int year)
    {
        var id = Guid.NewGuid();
        var result = InventoryAsset.Create(
            id, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Model X", 1, "{}", FixedNow,
            CreateValidRate(id),
            manufactureYear: year);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_MANUFACTURE_YEAR", result.Error.Code);
    }

    [Fact]
    public void Create_ManufactureYearBoundary1900AndCurrentVietnamYear_Succeeds()
    {
        var currentYear = BusinessDateTime.ToVietnamDate(FixedNow).Year;

        var id1 = Guid.NewGuid();
        var r1 = InventoryAsset.Create(
            id1, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Model X", 1, "{}", FixedNow,
            CreateValidRate(id1),
            manufactureYear: 1900);
        Assert.True(r1.IsSuccess);
        Assert.Equal(1900, r1.Value.ManufactureYear);

        var id2 = Guid.NewGuid();
        var r2 = InventoryAsset.Create(
            id2, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Model X", 1, "{}", FixedNow,
            CreateValidRate(id2),
            manufactureYear: currentYear);
        Assert.True(r2.IsSuccess);
        Assert.Equal(currentYear, r2.Value.ManufactureYear);
    }

    [Fact]
    public void Create_EmptyGuidThumbnail_Fails()
    {
        var id = Guid.NewGuid();
        var result = InventoryAsset.Create(
            id, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Model X", 1, "{}", FixedNow,
            CreateValidRate(id),
            thumbnailMediaId: Guid.Empty);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_THUMBNAIL_MEDIA_ID", result.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_InvalidSchemaVersion_Fails(int version)
    {
        var id = Guid.NewGuid();
        var result = InventoryAsset.Create(
            id, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Model X", version, "{}", FixedNow, CreateValidRate(id));
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_SCHEMA_VERSION", result.Error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("\"string\"")]
    [InlineData("123")]
    [InlineData("not-json")]
    public void Create_InvalidSpecValues_Fails(string specs)
    {
        var id = Guid.NewGuid();
        var result = InventoryAsset.Create(
            id, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Model X", 1, specs, FixedNow, CreateValidRate(id));
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_SPEC_VALUES", result.Error.Code);
    }

    [Fact]
    public void Create_EmptyIdentities_Fails()
    {
        var id = Guid.NewGuid();
        var r1 = InventoryAsset.Create(Guid.Empty, TestOwnerId, TestEquipmentTypeId, TestBrandId, "M", 1, "{}", FixedNow, CreateValidRate(id));
        Assert.Equal("INVALID_ASSET_ID", r1.Error.Code);

        var r2 = InventoryAsset.Create(id, Guid.Empty, TestEquipmentTypeId, TestBrandId, "M", 1, "{}", FixedNow, CreateValidRate(id));
        Assert.Equal("INVALID_OWNER_USER_ID", r2.Error.Code);

        var r3 = InventoryAsset.Create(id, TestOwnerId, Guid.Empty, TestBrandId, "M", 1, "{}", FixedNow, CreateValidRate(id));
        Assert.Equal("INVALID_EQUIPMENT_TYPE_ID", r3.Error.Code);

        var r4 = InventoryAsset.Create(id, TestOwnerId, TestEquipmentTypeId, Guid.Empty, "M", 1, "{}", FixedNow, CreateValidRate(id));
        Assert.Equal("INVALID_BRAND_ID", r4.Error.Code);
    }

    [Fact]
    public void UpdateDetails_ValidValues_UpdatesAllAndMutatesTime()
    {
        var id = Guid.NewGuid();
        var asset = InventoryAsset.Create(
            id, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Model A", 1, "{}", FixedNow, CreateValidRate(id)).Value;

        var updateTime = FixedNow.AddHours(2);
        var thumb = Guid.NewGuid();
        var newBrand = Guid.NewGuid();
        var result = asset.UpdateDetails(newBrand, "Model B", "SN-999", 2025, thumb, 2, "{\"color\":\"black\"}", updateTime);

        Assert.True(result.IsSuccess);
        Assert.Equal(newBrand, asset.BrandId);
        Assert.Equal("Model B", asset.Model);
        Assert.Equal("SN-999", asset.SerialNumber);
        Assert.Equal(2025, asset.ManufactureYear);
        Assert.Equal(thumb, asset.ThumbnailMediaId);
        Assert.Equal(2, asset.SchemaVersion);
        Assert.Equal("{\"color\":\"black\"}", asset.SpecValues);
        Assert.Equal(updateTime, asset.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_CannotRemoveExistingBrand()
    {
        var id = Guid.NewGuid();
        var asset = InventoryAsset.Create(
            id, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Model A", 1, "{}", FixedNow, CreateValidRate(id)).Value;

        var result = asset.UpdateDetails(null, "Model B", null, null, null, 1, "{}", FixedNow.AddHours(1));

        Assert.True(result.IsFailure);
        Assert.Equal("CANNOT_REMOVE_BRAND", result.Error.Code);
        Assert.Equal(TestBrandId, asset.BrandId);
    }

    [Fact]
    public void UpdateDetails_EmptyGuidBrand_Fails()
    {
        var id = Guid.NewGuid();
        var asset = InventoryAsset.Create(
            id, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Model A", 1, "{}", FixedNow, CreateValidRate(id)).Value;

        var result = asset.UpdateDetails(Guid.Empty, "Model B", null, null, null, 1, "{}", FixedNow.AddHours(1));

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_BRAND_ID", result.Error.Code);
    }

    [Fact]
    public void UpdateDetails_InvalidValue_DoesNotMutateState()
    {
        var id = Guid.NewGuid();
        var asset = InventoryAsset.Create(
            id, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Model Original", 1, "{\"k\":\"v\"}", FixedNow,
            CreateValidRate(id),
            serialNumber: "SN-ORIG",
            manufactureYear: 2020).Value;

        var invalidResult = asset.UpdateDetails(
            TestBrandId,
            "   ", // Invalid model
            "SN-NEW",
            2025,
            null,
            2,
            "{}",
            FixedNow.AddDays(1));

        Assert.True(invalidResult.IsFailure);
        Assert.Equal("INVALID_MODEL", invalidResult.Error.Code);

        // State remains pristine
        Assert.Equal(TestBrandId, asset.BrandId);
        Assert.Equal("Model Original", asset.Model);
        Assert.Equal("SN-ORIG", asset.SerialNumber);
        Assert.Equal(2020, asset.ManufactureYear);
        Assert.Equal(1, asset.SchemaVersion);
        Assert.Equal("{\"k\":\"v\"}", asset.SpecValues);
        Assert.Equal(FixedNow, asset.UpdatedAt);
    }

    [Fact]
    public void BookingToggle_EnableAndDisable_WorksAsExpected()
    {
        var id = Guid.NewGuid();
        var asset = InventoryAsset.Create(
            id, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Model A", 1, "{}", FixedNow, CreateValidRate(id)).Value;

        Assert.True(asset.CanAcceptBookings());

        // Disable with reason
        var disableTime = FixedNow.AddHours(1);
        var disRes = asset.DisableBookings("Under maintenance", disableTime);
        Assert.True(disRes.IsSuccess);
        Assert.False(asset.AcceptingBookings);
        Assert.Equal("Under maintenance", asset.SuspensionReason);
        Assert.Equal(disableTime, asset.UpdatedAt);

        // Enable again clears reason
        var enableTime = FixedNow.AddHours(2);
        var enRes = asset.EnableBookings(enableTime);
        Assert.True(enRes.IsSuccess);
        Assert.True(asset.AcceptingBookings);
        Assert.Null(asset.SuspensionReason);
        Assert.Equal(enableTime, asset.UpdatedAt);
    }
}
