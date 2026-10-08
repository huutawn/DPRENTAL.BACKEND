using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

public sealed class EquipmentListingDomainTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TestListingId = Guid.NewGuid();
    private static readonly Guid TestOwnerId = Guid.NewGuid();
    private static readonly Guid TestEquipmentTypeId = Guid.NewGuid();
    private static readonly Guid TestBrandId = Guid.NewGuid();

    [Fact]
    public void Create_ValidAllFields_SucceedsWithDraftStatus()
    {
        var result = EquipmentListing.Create(
            TestListingId,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "Sony FX3",
            1,
            "{\"sensor\":\"Full-Frame\"}",
            500_000m,
            3_000_000m,
            10_000_000m,
            "VND",
            FixedNow);

        Assert.True(result.IsSuccess);
        var listing = result.Value;
        Assert.Equal(TestListingId, listing.Id);
        Assert.Equal(TestOwnerId, listing.OwnerUserId);
        Assert.Equal(TestEquipmentTypeId, listing.EquipmentTypeId);
        Assert.Equal(TestBrandId, listing.BrandId);
        Assert.Equal("Sony FX3", listing.Model);
        Assert.Equal(1, listing.SchemaVersion);
        Assert.Equal(EquipmentListingStatus.Draft, listing.Status);
        Assert.Equal(500_000m, listing.DailyRate);
        Assert.Equal(3_000_000m, listing.WeeklyRate);
        Assert.Equal(10_000_000m, listing.MonthlyRate);
        Assert.Equal("VND", listing.Currency);
        Assert.Equal(FixedNow, listing.CreatedAt);
        Assert.Equal(FixedNow, listing.UpdatedAt);
    }

    [Fact]
    public void Create_EmptyListingId_FailsValidation()
    {
        var result = EquipmentListing.Create(
            Guid.Empty,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "Sony FX3",
            1,
            "{}",
            500_000m,
            null,
            null,
            "VND",
            FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_LISTING_ID", result.Error.Code);
    }

    [Fact]
    public void Create_EmptyBrandId_FailsValidation()
    {
        var result = EquipmentListing.Create(
            TestListingId,
            TestOwnerId,
            TestEquipmentTypeId,
            Guid.Empty,
            "Sony FX3",
            1,
            "{}",
            500_000m,
            null,
            null,
            "VND",
            FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_BRAND_ID", result.Error.Code);
    }

    [Fact]
    public void Create_InvalidDailyRate_FailsValidation()
    {
        var resultNegative = EquipmentListing.Create(
            TestListingId,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "Sony FX3",
            1,
            "{}",
            -100m,
            null,
            null,
            "VND",
            FixedNow);

        Assert.True(resultNegative.IsFailure);
        Assert.Equal("INVALID_DAILY_RATE", resultNegative.Error.Code);

        var resultFractional = EquipmentListing.Create(
            TestListingId,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "Sony FX3",
            1,
            "{}",
            500.5m,
            null,
            null,
            "VND",
            FixedNow);

        Assert.True(resultFractional.IsFailure);
        Assert.Equal("INVALID_DAILY_RATE", resultFractional.Error.Code);
    }

    [Fact]
    public void Create_InvalidCurrency_FailsValidation()
    {
        var result = EquipmentListing.Create(
            TestListingId,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "Sony FX3",
            1,
            "{}",
            500_000m,
            null,
            null,
            "USD",
            FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_CURRENCY", result.Error.Code);
    }

    [Fact]
    public void UpdateStatus_ToActiveWithoutAcceptingUnits_FailsValidation()
    {
        var listing = EquipmentListing.Create(
            TestListingId,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "Sony FX3",
            1,
            "{}",
            500_000m,
            null,
            null,
            "VND",
            FixedNow).Value;

        var result = listing.UpdateStatus(EquipmentListingStatus.Active, hasUnitAcceptingBookings: false, FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("LISTING_NEEDS_ACTIVE_UNIT", result.Error.Code);
        Assert.Equal(EquipmentListingStatus.Draft, listing.Status);
    }

    [Fact]
    public void UpdateStatus_ToActiveWithAcceptingUnits_Succeeds()
    {
        var listing = EquipmentListing.Create(
            TestListingId,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "Sony FX3",
            1,
            "{}",
            500_000m,
            null,
            null,
            "VND",
            FixedNow).Value;

        var result = listing.UpdateStatus(EquipmentListingStatus.Active, hasUnitAcceptingBookings: true, FixedNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(EquipmentListingStatus.Active, listing.Status);
    }

    [Fact]
    public void UpdateRentalRate_ValidNewRates_UpdatesValues()
    {
        var listing = EquipmentListing.Create(
            TestListingId,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "Sony FX3",
            1,
            "{}",
            500_000m,
            null,
            null,
            "VND",
            FixedNow).Value;

        var updateTime = FixedNow.AddHours(2);
        var result = listing.UpdateRentalRate(600_000m, 3_500_000m, 12_000_000m, "VND", updateTime);

        Assert.True(result.IsSuccess);
        Assert.Equal(600_000m, listing.DailyRate);
        Assert.Equal(3_500_000m, listing.WeeklyRate);
        Assert.Equal(12_000_000m, listing.MonthlyRate);
        Assert.Equal(updateTime, listing.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_ValidDetails_UpdatesValues()
    {
        var listing = EquipmentListing.Create(
            TestListingId,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "Sony FX3",
            1,
            "{}",
            500_000m,
            null,
            null,
            "VND",
            FixedNow).Value;

        var newBrand = Guid.NewGuid();
        var updateTime = FixedNow.AddHours(1);
        var result = listing.UpdateDetails(newBrand, "Sony FX3 v2", null, 2, "{\"version\":2}", updateTime);

        Assert.True(result.IsSuccess);
        Assert.Equal(newBrand, listing.BrandId);
        Assert.Equal("Sony FX3 v2", listing.Model);
        Assert.Equal(2, listing.SchemaVersion);
        Assert.Equal("{\"version\":2}", listing.SpecValues);
        Assert.Equal(updateTime, listing.UpdatedAt);
    }
}
