using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

public sealed class AssetRentalRateDomainTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TestAssetId = Guid.NewGuid();
    private static readonly Guid TestOwnerId = Guid.NewGuid();
    private static readonly Guid TestEquipmentTypeId = Guid.NewGuid();
    private static readonly Guid TestBrandId = Guid.NewGuid();

    [Fact]
    public void Create_ValidAllFields_Succeeds()
    {
        var result = AssetRentalRate.Create(
            TestAssetId,
            500_000m,
            3_000_000m,
            10_000_000m,
            "VND",
            FixedNow);

        Assert.True(result.IsSuccess);
        var rate = result.Value;
        Assert.Equal(TestAssetId, rate.AssetId);
        Assert.Equal(500_000m, rate.DailyRate);
        Assert.Equal(3_000_000m, rate.WeeklyRate);
        Assert.Equal(10_000_000m, rate.MonthlyRate);
        Assert.Equal("VND", rate.Currency);
        Assert.Equal(FixedNow, rate.CreatedAt);
        Assert.Equal(FixedNow, rate.UpdatedAt);
    }

    [Fact]
    public void Create_ValidMinimalFields_SucceedsWithNullWeeklyAndMonthly()
    {
        var result = AssetRentalRate.Create(
            TestAssetId,
            250_000m,
            null,
            null,
            "VND",
            FixedNow);

        Assert.True(result.IsSuccess);
        var rate = result.Value;
        Assert.Equal(250_000m, rate.DailyRate);
        Assert.Null(rate.WeeklyRate);
        Assert.Null(rate.MonthlyRate);
        Assert.Equal("VND", rate.Currency);
    }

    [Fact]
    public void Create_EmptyAssetId_Fails()
    {
        var result = AssetRentalRate.Create(
            Guid.Empty,
            100_000m,
            null,
            null,
            "VND",
            FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_ASSET_ID", result.Error.Code);
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("vnd")]
    [InlineData("EUR")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("VNDD")]
    public void Create_InvalidCurrency_Fails(string currency)
    {
        var result = AssetRentalRate.Create(
            TestAssetId,
            100_000m,
            null,
            null,
            currency,
            FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_CURRENCY", result.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100_000)]
    public void Create_ZeroOrNegativeDailyRate_Fails(decimal dailyRate)
    {
        var result = AssetRentalRate.Create(
            TestAssetId,
            dailyRate,
            null,
            null,
            "VND",
            FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_DAILY_RATE", result.Error.Code);
    }

    [Theory]
    [InlineData(100.5)]
    [InlineData(0.01)]
    [InlineData(100000.00001)]
    public void Create_FractionalDailyRate_Fails(decimal dailyRate)
    {
        var result = AssetRentalRate.Create(
            TestAssetId,
            dailyRate,
            null,
            null,
            "VND",
            FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_DAILY_RATE", result.Error.Code);
    }

    [Fact]
    public void Create_DailyRateExceedsLimit_Fails()
    {
        var result = AssetRentalRate.Create(
            TestAssetId,
            1_000_000_000_000_000_000m, // 10^18, exceeds 999999999999999999
            null,
            null,
            "VND",
            FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_DAILY_RATE", result.Error.Code);
    }

    [Fact]
    public void Create_DailyRateBoundaries_Succeeds()
    {
        var minResult = AssetRentalRate.Create(TestAssetId, 1m, null, null, "VND", FixedNow);
        Assert.True(minResult.IsSuccess);
        Assert.Equal(1m, minResult.Value.DailyRate);

        var maxResult = AssetRentalRate.Create(TestAssetId, 999_999_999_999_999_999m, null, null, "VND", FixedNow);
        Assert.True(maxResult.IsSuccess);
        Assert.Equal(999_999_999_999_999_999m, maxResult.Value.DailyRate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    [InlineData(1000.5)]
    public void Create_InvalidWeeklyRate_Fails(decimal weeklyRate)
    {
        var result = AssetRentalRate.Create(
            TestAssetId,
            100_000m,
            weeklyRate,
            null,
            "VND",
            FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_WEEKLY_RATE", result.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    [InlineData(2000.75)]
    public void Create_InvalidMonthlyRate_Fails(decimal monthlyRate)
    {
        var result = AssetRentalRate.Create(
            TestAssetId,
            100_000m,
            null,
            monthlyRate,
            "VND",
            FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_MONTHLY_RATE", result.Error.Code);
    }

    [Fact]
    public void Create_IndependentRatiosAllowed_DoesNotEnforceWeeklyOrMonthlyHierarchy()
    {
        // Weekly rate can be greater than daily * 7 or less than daily, no enforcement
        var result = AssetRentalRate.Create(
            TestAssetId,
            100_000m,
            50_000m, // Less than daily!
            20_000m, // Less than weekly!
            "VND",
            FixedNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(100_000m, result.Value.DailyRate);
        Assert.Equal(50_000m, result.Value.WeeklyRate);
        Assert.Equal(20_000m, result.Value.MonthlyRate);
    }

    [Fact]
    public void UpdateRates_FullReplacement_UpdatesRatesAndTimestamps()
    {
        var rate = AssetRentalRate.Create(
            TestAssetId,
            100_000m,
            600_000m,
            2_000_000m,
            "VND",
            FixedNow).Value;

        var later = FixedNow.AddHours(3);
        var updateResult = rate.UpdateRates(200_000m, null, 3_000_000m, "VND", later);

        Assert.True(updateResult.IsSuccess);
        Assert.Equal(200_000m, rate.DailyRate);
        Assert.Null(rate.WeeklyRate); // Weekly cleared!
        Assert.Equal(3_000_000m, rate.MonthlyRate);
        Assert.Equal("VND", rate.Currency);
        Assert.Equal(FixedNow, rate.CreatedAt); // CreatedAt preserved
        Assert.Equal(later, rate.UpdatedAt); // UpdatedAt refreshed
    }

    [Fact]
    public void InventoryAsset_Create_NullRentalRate_Fails()
    {
        var assetId = Guid.NewGuid();
        var result = InventoryAsset.Create(
            assetId,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "Sony FX3",
            1,
            "{}",
            FixedNow,
            null!);

        Assert.True(result.IsFailure);
        Assert.Equal("RENTAL_RATE_REQUIRED", result.Error.Code);
    }

    [Fact]
    public void InventoryAsset_Create_MismatchedAssetId_Fails()
    {
        var assetId = Guid.NewGuid();
        var differentId = Guid.NewGuid();
        var rate = AssetRentalRate.Create(differentId, 100_000m, null, null, "VND", FixedNow).Value;

        var result = InventoryAsset.Create(
            assetId,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "Sony FX3",
            1,
            "{}",
            FixedNow,
            rate);

        Assert.True(result.IsFailure);
        Assert.Equal("RENTAL_RATE_ASSET_MISMATCH", result.Error.Code);
    }

    [Fact]
    public void InventoryAsset_UpdateRentalRate_UpdatesAggregateAndTimestamps()
    {
        var assetId = Guid.NewGuid();
        var rate = AssetRentalRate.Create(assetId, 100_000m, 600_000m, 2_000_000m, "VND", FixedNow).Value;
        var asset = InventoryAsset.Create(
            assetId,
            TestOwnerId,
            TestEquipmentTypeId,
            TestBrandId,
            "Sony FX3",
            1,
            "{}",
            FixedNow,
            rate).Value;

        var updateTime = FixedNow.AddDays(1);
        var updateResult = asset.UpdateRentalRate(300_000m, null, null, "VND", updateTime);

        Assert.True(updateResult.IsSuccess);
        Assert.Equal(300_000m, asset.RentalRate.DailyRate);
        Assert.Null(asset.RentalRate.WeeklyRate);
        Assert.Null(asset.RentalRate.MonthlyRate);
        Assert.Equal(FixedNow, asset.CreatedAt);
        Assert.Equal(FixedNow, asset.RentalRate.CreatedAt);
        Assert.Equal(updateTime, asset.UpdatedAt);
        Assert.Equal(updateTime, asset.RentalRate.UpdatedAt);
    }

    private sealed class StubCaller(Guid? userId, bool isHuman = true, bool isAuthenticated = true, bool isAdmin = false, HashSet<string>? permissions = null) : IInventoryCallerContext
    {
        public Guid? UserId => userId;
        public string? Subject => userId?.ToString();
        public bool IsService => !isHuman;
        public bool IsHuman => isHuman;
        public bool IsAdmin => isAdmin;
        public bool IsAuthenticated => isAuthenticated;
        public IReadOnlySet<string> Permissions => permissions ?? new HashSet<string>();
        public IReadOnlySet<string> Scopes => new HashSet<string>();
        public IReadOnlySet<string> Roles => isAdmin ? new HashSet<string> { "adm" } : new HashSet<string>();
        public Guid? OrganizationId => null;
    }

    [Fact]
    public void CanUpdateRentalRate_OwnerMatchingSub_AllowedEvenWithoutManagePermission()
    {
        var ownerId = Guid.NewGuid();
        // Caller has no permissions at all!
        var caller = new StubCaller(ownerId, isHuman: true, isAuthenticated: true, isAdmin: false, permissions: new HashSet<string>());

        var allowed = InventoryAccessEvaluator.CanUpdateRentalRate(caller, ownerId);
        Assert.True(allowed);
    }

    [Fact]
    public void CanUpdateRentalRate_AdminCrossOwner_Allowed()
    {
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var adminCaller = new StubCaller(adminId, isHuman: true, isAuthenticated: true, isAdmin: true);

        var allowed = InventoryAccessEvaluator.CanUpdateRentalRate(adminCaller, ownerId);
        Assert.True(allowed);
    }

    [Fact]
    public void CanUpdateRentalRate_DifferentUser_Denied()
    {
        var ownerId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var caller = new StubCaller(otherUserId, isHuman: true, isAuthenticated: true, isAdmin: false,
            permissions: new HashSet<string> { InventoryAccessEvaluator.PermissionAssetManage });

        var allowed = InventoryAccessEvaluator.CanUpdateRentalRate(caller, ownerId);
        Assert.False(allowed);
    }

    [Fact]
    public void CanUpdateRentalRate_ServicePrincipal_Denied()
    {
        var ownerId = Guid.NewGuid();
        var serviceCaller = new StubCaller(Guid.NewGuid(), isHuman: false, isAuthenticated: true, isAdmin: false);

        var allowed = InventoryAccessEvaluator.CanUpdateRentalRate(serviceCaller, ownerId);
        Assert.False(allowed);
    }

    [Fact]
    public void CanUpdateRentalRate_UnauthenticatedOrNullOwner_Denied()
    {
        var ownerId = Guid.NewGuid();
        var unauthCaller = new StubCaller(ownerId, isHuman: true, isAuthenticated: false);

        Assert.False(InventoryAccessEvaluator.CanUpdateRentalRate(unauthCaller, ownerId));
        Assert.False(InventoryAccessEvaluator.CanUpdateRentalRate(new StubCaller(ownerId), null));
        Assert.False(InventoryAccessEvaluator.CanUpdateRentalRate(new StubCaller(ownerId), Guid.Empty));
    }

    [Fact]
    public void AssetRentalRateDto_Validate_RejectsMissingDailyRateOrCurrency()
    {
        var missingDaily = new AssetRentalRateDto(null, 500_000m, null, "VND");
        var r1 = missingDaily.Validate();
        Assert.True(r1.IsFailure);
        Assert.Equal("DAILY_RATE_REQUIRED", r1.Error.Code);

        var missingCurrency = new AssetRentalRateDto(100_000m, null, null, null);
        var r2 = missingCurrency.Validate();
        Assert.True(r2.IsFailure);
        Assert.Equal("CURRENCY_REQUIRED", r2.Error.Code);

        var emptyCurrency = new AssetRentalRateDto(100_000m, null, null, "   ");
        var r3 = emptyCurrency.Validate();
        Assert.True(r3.IsFailure);
        Assert.Equal("CURRENCY_REQUIRED", r3.Error.Code);
    }

    [Fact]
    public void AssetRentalRateDto_DisallowsUnknownProperties()
    {
        var jsonWithUnknown = "{\"dailyRate\":100000,\"currency\":\"VND\",\"unknownField\":123}";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AssetRentalRateDto>(jsonWithUnknown));
    }

    [Fact]
    public void PatchInventoryAssetRequest_DisallowsRentalRate()
    {
        var patchJson = "{\"model\":\"FX3\",\"dailyRate\":100000}";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PatchInventoryAssetRequest>(patchJson));
    }

    [Theory]
    [InlineData("{\"dailyRate\":0.99999999999999999999999999999999999,\"currency\":\"VND\"}")]
    [InlineData("{\"dailyRate\":1000.000000000000000000000000000000001,\"currency\":\"VND\"}")]
    [InlineData("{\"dailyRate\":1000.5,\"currency\":\"VND\"}")]
    [InlineData("{\"dailyRate\":1.55e1,\"currency\":\"VND\"}")]
    [InlineData("{\"dailyRate\":1e-1,\"currency\":\"VND\"}")]
    [InlineData("{\"dailyRate\":1e9999,\"currency\":\"VND\"}")]
    [InlineData("{\"dailyRate\":1e-9999,\"currency\":\"VND\"}")]
    [InlineData("{\"dailyRate\":true,\"currency\":\"VND\"}")]
    [InlineData("{\"dailyRate\":\"100000\",\"currency\":\"VND\"}")]
    public void AssetRentalRateDto_Deserialization_RejectsFractionalAndNonIntegerNumbers(string rawJson)
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AssetRentalRateDto>(rawJson));
    }

    [Theory]
    [InlineData("{\"dailyRate\":1000,\"currency\":\"VND\"}", 1000)]
    [InlineData("{\"dailyRate\":1000.0,\"currency\":\"VND\"}", 1000)]
    [InlineData("{\"dailyRate\":1000.000,\"currency\":\"VND\"}", 1000)]
    [InlineData("{\"dailyRate\":1e3,\"currency\":\"VND\"}", 1000)]
    [InlineData("{\"dailyRate\":1.5e3,\"currency\":\"VND\"}", 1500)]
    [InlineData("{\"dailyRate\":1.50e1,\"currency\":\"VND\"}", 15)]
    [InlineData("{\"dailyRate\":150e-1,\"currency\":\"VND\"}", 15)]
    public void AssetRentalRateDto_Deserialization_AcceptsValidIntegerRepresentations(string rawJson, decimal expectedDaily)
    {
        var dto = JsonSerializer.Deserialize<AssetRentalRateDto>(rawJson);
        Assert.NotNull(dto);
        Assert.Equal(expectedDaily, dto.DailyRate);
        Assert.Null(dto.WeeklyRate);
        Assert.Null(dto.MonthlyRate);
        Assert.Equal("VND", dto.Currency);
    }

    [Fact]
    public void AssetRentalRateDto_Deserialization_AcceptsScientificNotationOnAllRateFields()
    {
        var json = "{\"dailyRate\":5e5,\"weeklyRate\":3.0e6,\"monthlyRate\":1e7,\"currency\":\"VND\"}";
        var dto = JsonSerializer.Deserialize<AssetRentalRateDto>(json);
        Assert.NotNull(dto);
        Assert.Equal(500_000m, dto.DailyRate);
        Assert.Equal(3_000_000m, dto.WeeklyRate);
        Assert.Equal(10_000_000m, dto.MonthlyRate);
        Assert.Equal("VND", dto.Currency);

        var validation = dto.Validate();
        Assert.True(validation.IsSuccess);
    }

    [Fact]
    public void AssetRentalRateDto_Deserialization_FractionalOptionalRates_ThrowsJsonException()
    {
        var jsonWeeklyFraction = "{\"dailyRate\":500000,\"weeklyRate\":3000000.5,\"currency\":\"VND\"}";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AssetRentalRateDto>(jsonWeeklyFraction));

        var jsonMonthlyFraction = "{\"dailyRate\":500000,\"monthlyRate\":10000000.00000000000000000000000000001,\"currency\":\"VND\"}";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AssetRentalRateDto>(jsonMonthlyFraction));
    }
}
