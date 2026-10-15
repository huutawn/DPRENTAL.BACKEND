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
        var media1 = Guid.NewGuid();
        var media2 = Guid.NewGuid();

        var result = EquipmentListing.Create(
            TestListingId,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "Sony FX3 kèm bộ phụ kiện",
            "Mô tả máy quay FX3",
            "Sony FX3",
            1,
            "{\"sensor\":\"Full-Frame\"}",
            500_000m,
            3_000_000m,
            10_000_000m,
            "VND",
            FixedNow,
            2024,
            [media1, media2],
            media2);

        Assert.True(result.IsSuccess);
        var listing = result.Value;
        Assert.Equal(TestListingId, listing.Id);
        Assert.Equal(TestOwnerId, listing.OwnerUserId);
        Assert.Equal(TestEquipmentTypeId, listing.EquipmentTypeId);
        Assert.Equal(TestBrandId, listing.BrandId);
        Assert.Equal("Sony FX3 kèm bộ phụ kiện", listing.Name);
        Assert.Equal("Mô tả máy quay FX3", listing.Description);
        Assert.Equal("Sony FX3", listing.Model);
        Assert.Equal(2024, listing.ManufactureYear);
        Assert.Equal([media1, media2], listing.MediaIds);
        Assert.Equal(media2, listing.ThumbnailMediaId);
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
            null,
            "Sony FX3",
            1,
            "{}",
            500_000m,
            null,
            null,
            "VND",
            FixedNow,
            2024);

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
            null,
            "Sony FX3",
            1,
            "{}",
            500_000m,
            null,
            null,
            "VND",
            FixedNow,
            2024);

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
            null,
            "Sony FX3",
            1,
            "{}",
            -100m,
            null,
            null,
            "VND",
            FixedNow,
            2024);

        Assert.True(resultNegative.IsFailure);
        Assert.Equal("INVALID_DAILY_RATE", resultNegative.Error.Code);

        var resultFractional = EquipmentListing.Create(
            TestListingId,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "Sony FX3",
            null,
            "Sony FX3",
            1,
            "{}",
            500.5m,
            null,
            null,
            "VND",
            FixedNow,
            2024);

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
            null,
            "Sony FX3",
            1,
            "{}",
            500_000m,
            null,
            null,
            "USD",
            FixedNow,
            2024);

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
            null,
            "Sony FX3",
            1,
            "{}",
            500_000m,
            null,
            null,
            "VND",
            FixedNow,
            2024).Value;

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
            null,
            "Sony FX3",
            1,
            "{}",
            500_000m,
            null,
            null,
            "VND",
            FixedNow,
            2024).Value;

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
            null,
            "Sony FX3",
            1,
            "{}",
            500_000m,
            null,
            null,
            "VND",
            FixedNow,
            2024).Value;

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
            null,
            "Sony FX3",
            1,
            "{}",
            500_000m,
            null,
            null,
            "VND",
            FixedNow,
            2022).Value;

        var newBrand = Guid.NewGuid();
        var mediaA = Guid.NewGuid();
        var mediaB = Guid.NewGuid();
        var updateTime = FixedNow.AddHours(1);
        var result = listing.UpdateDetails(newBrand, "Sony FX3 v2 Name", "New Desc", "Sony FX3 v2", 2025, [mediaB, mediaA], mediaA, 2, "{\"version\":2}", updateTime);

        Assert.True(result.IsSuccess);
        Assert.Equal(newBrand, listing.BrandId);
        Assert.Equal("Sony FX3 v2 Name", listing.Name);
        Assert.Equal("New Desc", listing.Description);
        Assert.Equal("Sony FX3 v2", listing.Model);
        Assert.Equal(2025, listing.ManufactureYear);
        Assert.Equal([mediaB, mediaA], listing.MediaIds);
        Assert.Equal(mediaA, listing.ThumbnailMediaId);
        Assert.Equal(2, listing.SchemaVersion);
        Assert.Equal("{\"version\":2}", listing.SpecValues);
        Assert.Equal(updateTime, listing.UpdatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_InvalidName_FailsValidation(string? invalidName)
    {
        var result = EquipmentListing.Create(
            TestListingId, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            invalidName!, null, "Sony FX3", 1, "{}", 500_000m, null, null, "VND", FixedNow, 2024);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_NAME", result.Error.Code);
    }

    [Fact]
    public void Create_NameTooLong_FailsValidation()
    {
        var longName = new string('A', 201);
        var result = EquipmentListing.Create(
            TestListingId, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            longName, null, "Sony FX3", 1, "{}", 500_000m, null, null, "VND", FixedNow, 2024);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_NAME", result.Error.Code);
    }

    [Fact]
    public void Create_DescriptionTooLong_FailsValidation()
    {
        var longDesc = new string('B', 2001);
        var result = EquipmentListing.Create(
            TestListingId, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Valid Name", longDesc, "Sony FX3", 1, "{}", 500_000m, null, null, "VND", FixedNow, 2024);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_DESCRIPTION", result.Error.Code);
    }

    [Fact]
    public void Create_WhitespaceDescription_NormalizesToNull()
    {
        var result = EquipmentListing.Create(
            TestListingId, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Valid Name", "   ", "Sony FX3", 1, "{}", 500_000m, null, null, "VND", FixedNow, 2024);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Description);
    }

    [Fact]
    public void UpdateStatus_InvalidEnumValue_FailsValidation()
    {
        var listing = EquipmentListing.Create(
            TestListingId, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Valid Name", null, "Sony FX3", 1, "{}", 500_000m, null, null, "VND", FixedNow, 2024).Value;

        var result = listing.UpdateStatus((EquipmentListingStatus)999, false, FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_STATUS", result.Error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1899)]
    [InlineData(2027)]
    public void Create_InvalidManufactureYear_FailsValidation(int? invalidYear)
    {
        var result = EquipmentListing.Create(
            TestListingId, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Valid Name", null, "Sony FX3", 1, "{}", 500_000m, null, null, "VND", FixedNow, invalidYear);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_MANUFACTURE_YEAR", result.Error.Code);
    }

    [Fact]
    public void ValidateManufactureYear_UsesVietnamTimeZoneBoundary()
    {
        // 2026-12-31 18:00:00 UTC == 2027-01-01 01:00:00 ICT (Vietnam)
        var utcNewYearEveInVietnamNewYear = new DateTimeOffset(2026, 12, 31, 18, 0, 0, TimeSpan.Zero);

        var validInVietnam2027 = EquipmentListing.ValidateManufactureYear(2027, utcNewYearEveInVietnamNewYear);
        Assert.True(validInVietnam2027.IsSuccess);
        Assert.Equal(2027, validInVietnam2027.Value);

        var invalidInVietnam2028 = EquipmentListing.ValidateManufactureYear(2028, utcNewYearEveInVietnamNewYear);
        Assert.True(invalidInVietnam2028.IsFailure);
        Assert.Equal("INVALID_MANUFACTURE_YEAR", invalidInVietnam2028.Error.Code);
    }

    [Fact]
    public void ValidateDetails_MediaIdsAndThumbnailRules_Enforced()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        // 1. Empty GUID in mediaIds
        var emptyInList = EquipmentListing.ValidateDetails("Name", null, "Model", [id1, Guid.Empty], null, 1, "{}");
        Assert.True(emptyInList.IsFailure);
        Assert.Equal("INVALID_MEDIA_ID", emptyInList.Error.Code);

        // 2. Duplicate GUID in mediaIds
        var dupInList = EquipmentListing.ValidateDetails("Name", null, "Model", [id1, id2, id1], null, 1, "{}");
        Assert.True(dupInList.IsFailure);
        Assert.Equal("DUPLICATE_MEDIA_ID", dupInList.Error.Code);

        // 3. Exceeding 20 items
        var twentyOneIds = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToList();
        var tooMany = EquipmentListing.ValidateDetails("Name", null, "Model", twentyOneIds, null, 1, "{}");
        Assert.True(tooMany.IsFailure);
        Assert.Equal("MEDIA_LIMIT_EXCEEDED", tooMany.Error.Code);

        // 4. Exactly 20 items succeeds
        var twentyIds = twentyOneIds.Take(20).ToList();
        var twentyOk = EquipmentListing.ValidateDetails("Name", null, "Model", twentyIds, twentyIds[19], 1, "{}");
        Assert.True(twentyOk.IsSuccess);
        Assert.Equal(20, twentyOk.Value.MediaIds.Length);

        // 5. Thumbnail not in mediaIds fails
        var thumbOutside = EquipmentListing.ValidateDetails("Name", null, "Model", [id1], id2, 1, "{}");
        Assert.True(thumbOutside.IsFailure);
        Assert.Equal("INVALID_THUMBNAIL_MEDIA_ID", thumbOutside.Error.Code);
    }

    [Fact]
    public void Create_DepositAmount_DefaultZero_Succeeds()
    {
        var listing = EquipmentListing.Create(
            TestListingId, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Sony FX3", null, "Sony FX3", 1, "{}", 500_000m, null, null, "VND", FixedNow, 2024).Value;

        Assert.Equal(0m, listing.DepositAmount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500000)]
    [InlineData(999999999999999999)]
    public void Create_DepositAmount_ValidValues_Succeeds(decimal depositAmount)
    {
        var result = EquipmentListing.Create(
            TestListingId, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Sony FX3", null, "Sony FX3", 1, "{}", 500_000m, null, null, "VND", FixedNow, 2024,
            depositAmount: depositAmount);

        Assert.True(result.IsSuccess);
        Assert.Equal(depositAmount, result.Value.DepositAmount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-500000)]
    [InlineData(0.5)]
    [InlineData(500000.99)]
    [InlineData(1000000000000000000)]
    public void Create_DepositAmount_InvalidValues_ReturnsInvalidDepositAmount(decimal depositAmount)
    {
        var result = EquipmentListing.Create(
            TestListingId, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Sony FX3", null, "Sony FX3", 1, "{}", 500_000m, null, null, "VND", FixedNow, 2024,
            depositAmount: depositAmount);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_DEPOSIT_AMOUNT", result.Error.Code);
    }

    [Fact]
    public void UpdateDepositAmount_Valid_UpdatesValueAndTimestamp()
    {
        var listing = EquipmentListing.Create(
            TestListingId, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Sony FX3", null, "Sony FX3", 1, "{}", 500_000m, null, null, "VND", FixedNow, 2024,
            depositAmount: 500_000m).Value;

        var later = FixedNow.AddHours(2);
        var result = listing.UpdateDepositAmount(0m, later);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, listing.DepositAmount);
        Assert.Equal(later, listing.UpdatedAt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(500000.5)]
    [InlineData(1000000000000000000)]
    public void UpdateDepositAmount_Invalid_ReturnsInvalidDepositAmount(decimal depositAmount)
    {
        var listing = EquipmentListing.Create(
            TestListingId, TestOwnerId, TestEquipmentTypeId, TestBrandId,
            "Sony FX3", null, "Sony FX3", 1, "{}", 500_000m, null, null, "VND", FixedNow, 2024).Value;

        var result = listing.UpdateDepositAmount(depositAmount, FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_DEPOSIT_AMOUNT", result.Error.Code);
    }
}
