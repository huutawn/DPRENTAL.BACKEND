using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class AssetAndListingAvailabilityCheckTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly FixedToday = BusinessDateTime.ToVietnamDate(FixedNow);

    public AssetAndListingAvailabilityCheckTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private InventorySecurityTestFactory CreateFactory(TestTimeProvider? clock = null)
    {
        clock ??= new TestTimeProvider(FixedNow);
        return new InventorySecurityTestFactory(_fixture.ConnectionString, clock);
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    private async Task<(EquipmentListing Listing, List<InventoryAsset> Assets)> CreateListingWithUnitsAsync(
        Guid ownerId,
        int unitCount,
        EquipmentListingStatus status = EquipmentListingStatus.Active,
        string serialPrefix = "AVAIL")
    {
        await using var db = CreateDbContext();
        var listing = EquipmentListing.Create(
            Guid.NewGuid(),
            ownerId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Camera Sony",
            1,
            "{}",
            100_000m,
            null,
            null,
            "VND",
            FixedNow).Value;

        listing.UpdateStatus(status, true, FixedNow);
        db.EquipmentListings.Add(listing);

        var assets = new List<InventoryAsset>();
        for (int i = 1; i <= unitCount; i++)
        {
            var unit = InventoryAsset.Create(
                Guid.NewGuid(),
                listing.Id,
                $"{serialPrefix}-{i:D4}-{Guid.NewGuid():N}",
                FixedNow).Value;
            db.Assets.Add(unit);
            assets.Add(unit);
        }

        await db.SaveChangesAsync();
        return (listing, assets);
    }

    [Fact]
    public async Task CheckListingAvailability_QuantityLimitsAndDefaults()
    {
        var clock = new TestTimeProvider(FixedNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var (listing, _) = await CreateListingWithUnitsAsync(ownerId, 2);

        var token = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. RequestedQuantity = 0 -> 400
        var zeroResp = await client.PostAsJsonAsync(
            $"/api/v1/inventory/equipment-listings/{listing.Id}/availability/check",
            new { startDate = "2026-10-10", endDate = "2026-10-12", requestedQuantity = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, zeroResp.StatusCode);

        // 2. RequestedQuantity = 501 -> 400
        var excessResp = await client.PostAsJsonAsync(
            $"/api/v1/inventory/equipment-listings/{listing.Id}/availability/check",
            new { startDate = "2026-10-10", endDate = "2026-10-12", requestedQuantity = 501 });
        Assert.Equal(HttpStatusCode.BadRequest, excessResp.StatusCode);

        // 3. Omitted requestedQuantity -> defaults to 1 and succeeds
        var omittedResp = await client.PostAsJsonAsync(
            $"/api/v1/inventory/equipment-listings/{listing.Id}/availability/check",
            new { startDate = "2026-10-10", endDate = "2026-10-12" });
        Assert.Equal(HttpStatusCode.OK, omittedResp.StatusCode);
        var omittedBody = await omittedResp.Content.ReadFromJsonAsync<CheckListingAvailabilityResponse>();
        Assert.NotNull(omittedBody);
        Assert.Equal(1, omittedBody.RequestedQuantity);
        Assert.Equal(2, omittedBody.AvailableQuantity);
        Assert.True(omittedBody.IsAvailable);
    }
    [Fact]
    public async Task CheckAvailability_EmptyBodyOrMissingOrNullDates_Returns400MissingDateParameters()
    {
        var clock = new TestTimeProvider(FixedNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var (listing, units) = await CreateListingWithUnitsAsync(ownerId, 1);
        var asset = units[0];

        var token = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Listing check with empty body {}
        var emptyListingResp = await client.PostAsJsonAsync(
            $"/api/v1/inventory/equipment-listings/{listing.Id}/availability/check",
            new { });
        Assert.Equal(HttpStatusCode.BadRequest, emptyListingResp.StatusCode);
        var emptyListingBody = await emptyListingResp.Content.ReadAsStringAsync();
        Assert.Contains("MISSING_DATE_PARAMETERS", emptyListingBody);

        // 2. Listing check with only requestedQuantity
        var qtyOnlyListingResp = await client.PostAsJsonAsync(
            $"/api/v1/inventory/equipment-listings/{listing.Id}/availability/check",
            new { requestedQuantity = 3 });
        Assert.Equal(HttpStatusCode.BadRequest, qtyOnlyListingResp.StatusCode);
        var qtyOnlyBody = await qtyOnlyListingResp.Content.ReadAsStringAsync();
        Assert.Contains("MISSING_DATE_PARAMETERS", qtyOnlyBody);

        // 3. Listing check with null startDate
        var nullStartListingResp = await client.PostAsJsonAsync(
            $"/api/v1/inventory/equipment-listings/{listing.Id}/availability/check",
            new { startDate = (string?)null, endDate = "2026-10-12" });
        Assert.Equal(HttpStatusCode.BadRequest, nullStartListingResp.StatusCode);

        // 4. Listing check with reversed dates (start > end)
        var reversedListingResp = await client.PostAsJsonAsync(
            $"/api/v1/inventory/equipment-listings/{listing.Id}/availability/check",
            new { startDate = "2026-10-15", endDate = "2026-10-10" });
        Assert.Equal(HttpStatusCode.BadRequest, reversedListingResp.StatusCode);
        var reversedBody = await reversedListingResp.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_DATE_ORDER", reversedBody);

        // 5. Asset check with empty body {}
        var emptyAssetResp = await client.PostAsJsonAsync(
            $"/api/v1/inventory/assets/{asset.Id}/availability/check",
            new { });
        Assert.Equal(HttpStatusCode.BadRequest, emptyAssetResp.StatusCode);
        var emptyAssetBody = await emptyAssetResp.Content.ReadAsStringAsync();
        Assert.Contains("MISSING_DATE_PARAMETERS", emptyAssetBody);

        // 6. Asset check with reversed dates
        var reversedAssetResp = await client.PostAsJsonAsync(
            $"/api/v1/inventory/assets/{asset.Id}/availability/check",
            new { startDate = "2026-10-15", endDate = "2026-10-10" });
        Assert.Equal(HttpStatusCode.BadRequest, reversedAssetResp.StatusCode);
        var reversedAssetBody = await reversedAssetResp.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_DATE_ORDER", reversedAssetBody);
    }

    [Fact]
    public async Task CheckListingAvailability_DraftOrInactiveListing_ReturnsUnavailable()
    {
        var clock = new TestTimeProvider(FixedNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var (draftListing, _) = await CreateListingWithUnitsAsync(ownerId, 3, EquipmentListingStatus.Draft);

        var token = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/inventory/equipment-listings/{draftListing.Id}/availability/check",
            new { startDate = "2026-10-10", endDate = "2026-10-12", requestedQuantity = 1 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CheckListingAvailabilityResponse>();
        Assert.NotNull(body);
        Assert.Equal(0, body.AvailableQuantity);
        Assert.False(body.IsAvailable);
    }

    [Fact]
    public async Task CheckListingAvailability_AlternatingUnits_EvaluatesUnavailableAcrossFullInterval()
    {
        var clock = new TestTimeProvider(FixedNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var (listing, units) = await CreateListingWithUnitsAsync(ownerId, 2);

        // Unit 1 is blocked on Monday (2026-10-12)
        // Unit 2 is blocked on Tuesday (2026-10-13)
        await using (var db = CreateDbContext())
        {
            var b1 = CalendarBlock.Create(
                Guid.NewGuid(), units[0].Id,
                new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 12),
                CalendarBlockReason.Maintenance, "op", FixedNow, FixedToday).Value;
            var b2 = CalendarBlock.Create(
                Guid.NewGuid(), units[1].Id,
                new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 13),
                CalendarBlockReason.Maintenance, "op", FixedNow, FixedToday).Value;
            db.CalendarBlocks.AddRange(b1, b2);
            await db.SaveChangesAsync();
        }

        var token = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Checking interval [2026-10-12, 2026-10-13]: neither unit is free across the FULL interval!
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/inventory/equipment-listings/{listing.Id}/availability/check",
            new { startDate = "2026-10-12", endDate = "2026-10-13", requestedQuantity = 1 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CheckListingAvailabilityResponse>();
        Assert.NotNull(body);
        Assert.Equal(0, body.AvailableQuantity);
        Assert.False(body.IsAvailable);
    }

    [Fact]
    public async Task CheckAssetAvailability_RejectsQuantityGreaterThanOne()
    {
        var clock = new TestTimeProvider(FixedNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var (_, units) = await CreateListingWithUnitsAsync(ownerId, 1);
        var asset = units[0];

        var token = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Quantity = 2 -> 400
        var resp2 = await client.PostAsJsonAsync(
            $"/api/v1/inventory/assets/{asset.Id}/availability/check",
            new { startDate = "2026-10-10", endDate = "2026-10-12", requestedQuantity = 2 });
        Assert.Equal(HttpStatusCode.BadRequest, resp2.StatusCode);

        // Quantity = 0 -> 400
        var resp0 = await client.PostAsJsonAsync(
            $"/api/v1/inventory/assets/{asset.Id}/availability/check",
            new { startDate = "2026-10-10", endDate = "2026-10-12", requestedQuantity = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, resp0.StatusCode);
    }

    [Fact]
    public async Task CheckAssetAvailability_SuccessWhenFree_AndUnavailableWhenBlockedOrInactiveListing()
    {
        var clock = new TestTimeProvider(FixedNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var (activeListing, activeUnits) = await CreateListingWithUnitsAsync(ownerId, 2);
        var freeAsset = activeUnits[0];
        var blockedAsset = activeUnits[1];

        await using (var db = CreateDbContext())
        {
            var b = CalendarBlock.Create(
                Guid.NewGuid(), blockedAsset.Id,
                new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12),
                CalendarBlockReason.Maintenance, "op", FixedNow, FixedToday).Value;
            db.CalendarBlocks.Add(b);
            await db.SaveChangesAsync();
        }

        var (inactiveListing, inactiveUnits) = await CreateListingWithUnitsAsync(ownerId, 1, EquipmentListingStatus.Inactive);
        var inactiveAsset = inactiveUnits[0];

        var token = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Free asset on active listing -> isAvailable = true, availableQuantity = 1
        var freeResp = await client.PostAsJsonAsync(
            $"/api/v1/inventory/assets/{freeAsset.Id}/availability/check",
            new { startDate = "2026-10-10", endDate = "2026-10-12" });
        Assert.Equal(HttpStatusCode.OK, freeResp.StatusCode);
        var freeBody = await freeResp.Content.ReadFromJsonAsync<CheckAssetAvailabilityResponse>();
        Assert.NotNull(freeBody);
        Assert.Equal(freeAsset.Id, freeBody.AssetId);
        Assert.Equal(1, freeBody.RequestedQuantity);
        Assert.Equal(1, freeBody.AvailableQuantity);
        Assert.True(freeBody.IsAvailable);

        // 2. Blocked asset -> isAvailable = false, availableQuantity = 0
        var blockedResp = await client.PostAsJsonAsync(
            $"/api/v1/inventory/assets/{blockedAsset.Id}/availability/check",
            new { startDate = "2026-10-10", endDate = "2026-10-12", requestedQuantity = 1 });
        Assert.Equal(HttpStatusCode.OK, blockedResp.StatusCode);
        var blockedBody = await blockedResp.Content.ReadFromJsonAsync<CheckAssetAvailabilityResponse>();
        Assert.NotNull(blockedBody);
        Assert.Equal(0, blockedBody.AvailableQuantity);
        Assert.False(blockedBody.IsAvailable);

        // 3. Asset belonging to Inactive listing -> isAvailable = false, availableQuantity = 0
        var inactiveResp = await client.PostAsJsonAsync(
            $"/api/v1/inventory/assets/{inactiveAsset.Id}/availability/check",
            new { startDate = "2026-10-10", endDate = "2026-10-12", requestedQuantity = 1 });
        Assert.Equal(HttpStatusCode.OK, inactiveResp.StatusCode);
        var inactiveBody = await inactiveResp.Content.ReadFromJsonAsync<CheckAssetAvailabilityResponse>();
        Assert.NotNull(inactiveBody);
        Assert.Equal(0, inactiveBody.AvailableQuantity);
        Assert.False(inactiveBody.IsAvailable);

        // 4. Asset not found -> 404
        var notFoundResp = await client.PostAsJsonAsync(
            $"/api/v1/inventory/assets/{Guid.NewGuid()}/availability/check",
            new { startDate = "2026-10-10", endDate = "2026-10-12" });
        Assert.Equal(HttpStatusCode.NotFound, notFoundResp.StatusCode);
    }

    [Fact]
    public async Task CheckBatchAvailability_InactiveParentListing_ReturnsUnavailable()
    {
        var clock = new TestTimeProvider(FixedNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var (activeListing, activeUnits) = await CreateListingWithUnitsAsync(ownerId, 1);
        var (draftListing, draftUnits) = await CreateListingWithUnitsAsync(ownerId, 1, EquipmentListingStatus.Draft);

        var token = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var batchReq = new CheckAvailabilityBatchRequest(
            new[] { activeUnits[0].Id, draftUnits[0].Id },
            new DateOnly(2026, 10, 10),
            new DateOnly(2026, 10, 12));

        var resp = await client.PostAsJsonAsync("/api/v1/inventory/availability/check", batchReq);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<CheckAvailabilityBatchResponse>();
        Assert.NotNull(body);
        // draftUnits[0] is marked as unavailable because parent listing is not Active!
        Assert.Contains(draftUnits[0].Id, body.UnavailableAssetIds);
        Assert.DoesNotContain(activeUnits[0].Id, body.UnavailableAssetIds);
    }
}
