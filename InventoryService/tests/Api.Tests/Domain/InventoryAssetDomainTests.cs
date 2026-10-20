using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

public sealed class InventoryAssetDomainTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ValidParameters_SucceedsWithAssetCode()
    {
        var id = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        var result = InventoryAsset.Create(id, listingId, "SN-12345", FixedNow, 2024);

        Assert.True(result.IsSuccess);
        var asset = result.Value;
        Assert.Equal(id, asset.Id);
        Assert.Equal(listingId, asset.EquipmentListingId);
        Assert.Equal($"AST-{id:N}", asset.AssetCode);
        Assert.Equal("SN-12345", asset.SerialNumber);
        Assert.Equal(2024, asset.ManufactureYear);
        Assert.True(asset.AcceptingBookings);
        Assert.Null(asset.SuspensionReason);
        Assert.Equal(FixedNow, asset.CreatedAt);
        Assert.Equal(FixedNow, asset.UpdatedAt);
    }

    [Fact]
    public void Create_EmptyAssetId_FailsValidation()
    {
        var result = InventoryAsset.Create(Guid.Empty, Guid.NewGuid(), "SN-12345", FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_ASSET_ID", result.Error.Code);
    }

    [Fact]
    public void Create_EmptyListingId_FailsValidation()
    {
        var result = InventoryAsset.Create(Guid.NewGuid(), Guid.Empty, "SN-12345", FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_EQUIPMENT_LISTING_ID", result.Error.Code);
    }

    [Fact]
    public void Create_SerialNumberTooLong_FailsValidation()
    {
        var longSerial = new string('A', 101);
        var result = InventoryAsset.Create(Guid.NewGuid(), Guid.NewGuid(), longSerial, FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_SERIAL_NUMBER", result.Error.Code);
    }

    [Fact]
    public void Create_ManufactureYearOutOfRange_FailsValidation()
    {
        var resultTooOld = InventoryAsset.Create(Guid.NewGuid(), Guid.NewGuid(), "SN-12345", FixedNow, 1899);
        Assert.True(resultTooOld.IsFailure);
        Assert.Equal("INVALID_MANUFACTURE_YEAR", resultTooOld.Error.Code);

        int nextYear = BusinessDateTime.ToVietnamDate(FixedNow).Year + 1;
        var resultFuture = InventoryAsset.Create(Guid.NewGuid(), Guid.NewGuid(), "SN-12345", FixedNow, nextYear);
        Assert.True(resultFuture.IsFailure);
        Assert.Equal("INVALID_MANUFACTURE_YEAR", resultFuture.Error.Code);
    }

    [Fact]
    public void UpdatePhysicalDetails_ValidDetails_UpdatesValues()
    {
        var asset = InventoryAsset.Create(Guid.NewGuid(), Guid.NewGuid(), "SN-12345", FixedNow).Value;
        var updateTime = FixedNow.AddHours(2);

        var updateResult = asset.UpdatePhysicalDetails("SN-UPDATED", 2025, updateTime);

        Assert.True(updateResult.IsSuccess);
        Assert.Equal("SN-UPDATED", asset.SerialNumber);
        Assert.Equal(2025, asset.ManufactureYear);
        Assert.Equal(updateTime, asset.UpdatedAt);
    }

    [Fact]
    public void ToggleBookings_EnableAndDisable_WorksAsExpected()
    {
        var asset = InventoryAsset.Create(Guid.NewGuid(), Guid.NewGuid(), "SN-12345", FixedNow).Value;
        Assert.True(asset.CanAcceptBookings());

        var disableTime = FixedNow.AddHours(1);
        var disableResult = asset.DisableBookings("Maintenance", disableTime);

        Assert.True(disableResult.IsSuccess);
        Assert.False(asset.AcceptingBookings);
        Assert.Equal("Maintenance", asset.SuspensionReason);
        Assert.Equal(disableTime, asset.UpdatedAt);

        var enableTime = FixedNow.AddHours(2);
        var enableResult = asset.EnableBookings(enableTime);

        Assert.True(enableResult.IsSuccess);
        Assert.True(asset.AcceptingBookings);
        Assert.Null(asset.SuspensionReason);
        Assert.Equal(enableTime, asset.UpdatedAt);
    }

    [Fact]
    public void DisableBookings_ReasonTooLong_FailsValidation()
    {
        var asset = InventoryAsset.Create(Guid.NewGuid(), Guid.NewGuid(), "SN-12345", FixedNow).Value;
        var longReason = new string('A', 501);

        var result = asset.DisableBookings(longReason, FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("SUSPENSION_REASON_TOO_LONG", result.Error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_NullOrWhitespaceSerialNumber_FailsValidation(string? serial)
    {
        var result = InventoryAsset.Create(Guid.NewGuid(), Guid.NewGuid(), serial!, FixedNow);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_SERIAL_NUMBER", result.Error.Code);
    }
}
