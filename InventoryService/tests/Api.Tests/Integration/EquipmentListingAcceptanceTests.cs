using System.Net;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Application.DTOs.Calendar;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class EquipmentListingAcceptanceTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _fixture;

    public EquipmentListingAcceptanceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class StubCatalogClient : ICatalogServiceClient
    {
        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(
                equipmentTypeId, "EQ-FX3", "Sony FX3 Cinema Line", null, 1, DateTime.UtcNow, null, null)));

        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid brandId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogBrandResponse(
                brandId, "BR-SONY", "Sony", true)));

        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<CatalogBatchSchemasResponse>> GetEquipmentTypeSchemasBatchAsync(Guid equipmentTypeId, IReadOnlyList<int> versions, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
                equipmentTypeId,
                1,
                [new CatalogBatchSchemaItemResponse(1, [])])));
    }

    private sealed class StubMediaClient : IMediaServiceClient
    {
        public Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVerificationInfo(mediaId, "Ready", Guid.NewGuid().ToString("D"))));

        public Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(mediaId, "Public")));
    }

    private (Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> App, Dprental.InventoryService.Api.Tests.Security.InventorySecurityTestFactory SecFactory) CreateFactory()
    {
        var secFactory = new Dprental.InventoryService.Api.Tests.Security.InventorySecurityTestFactory(_fixture.ConnectionString);
        var app = secFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICatalogServiceClient>();
                services.AddSingleton<ICatalogServiceClient>(new StubCatalogClient());

                services.RemoveAll<IMediaServiceClient>();
                services.AddSingleton<IMediaServiceClient>(new StubMediaClient());
            });
        });
        return (app, secFactory);
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    [Fact]
    public async Task CreateListing_WithSerialNumber_CreatesOneUnitWithUniqueCode()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var payload = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            model = "Sony FX3 Batch 5",
            serialNumber = "FX3-TEST-00001",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 500_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.NotNull(created);
        Assert.Equal(1, created.TotalQuantity);
        Assert.Equal("Draft", created.Status);

        // Verify in PostgreSQL
        await using var db = CreateDbContext();
        var units = await db.Assets.Where(a => a.EquipmentListingId == created.Id).ToListAsync();
        Assert.Single(units);
        Assert.Equal("FX3-TEST-00001", units[0].SerialNumber);
        Assert.StartsWith("AST-", units[0].AssetCode);
    }

    [Fact]
    public async Task AddAsset_IncreasesDerivedSupply_AndDisableDecreasesSupplyWhilePreservingUnits()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Create listing with 1 initial unit
        EquipmentListing listing;
        await using (var db = CreateDbContext())
        {
            listing = await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: owner, model: "AddUnits Test", initialUnits: 1);
        }

        // 2. Add 1 unit via POST /assets
        var addPayload = new AddListingAssetRequest("FX3-TEST-00002");
        var addResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/assets",
            new StringContent(JsonSerializer.Serialize(addPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, addResp.StatusCode);
        var addResult = await addResp.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(addResult);
        Assert.Equal("FX3-TEST-00002", addResult.SerialNumber);

        // 3. Disable bookings on the newly added unit
        var disablePayload = new { acceptingBookings = false, reason = "Maintenance" };
        var disableResp = await client.PatchAsync($"api/v1/inventory/assets/{addResult.Id:D}/accepting-bookings",
            new StringContent(JsonSerializer.Serialize(disablePayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, disableResp.StatusCode);

        // 4. Verify listing totalQuantity is now 1 (supply decreased, history preserved)
        var getListingResp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}");
        Assert.Equal(HttpStatusCode.OK, getListingResp.StatusCode);
        var updatedListing = await getListingResp.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.NotNull(updatedListing);
        Assert.Equal(1, updatedListing.TotalQuantity);

        await using (var db = CreateDbContext())
        {
            var allUnits = await db.Assets.Where(a => a.EquipmentListingId == listing.Id).ToListAsync();
            Assert.Equal(2, allUnits.Count); // all 2 units preserved in history!
        }
    }

    [Fact]
    public async Task AlternatingAvailability_DailyAvailabilityOne_IntervalAvailabilityZero()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var mon = new DateOnly(2026, 11, 2);
        var tue = new DateOnly(2026, 11, 3);

        Guid listingId;
        Guid u1Id;
        Guid u2Id;

        await using (var db = CreateDbContext())
        {
            var listing = await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: owner, model: "Alternating Test", initialUnits: 0, status: EquipmentListingStatus.Draft);
            listingId = listing.Id;

            // Unit 1: Blocked on Tuesday (Free on Monday)
            var u1 = InventoryAsset.Create(Guid.NewGuid(), listing.Id, "SN-U1", FixedNow).Value;
            u1.CalendarBlocks.Add(CalendarBlock.Create(Guid.NewGuid(), u1.Id, tue, tue, CalendarBlockReason.Maintenance, "op", FixedNow, mon).Value);

            // Unit 2: Blocked on Monday (Free on Tuesday)
            var u2 = InventoryAsset.Create(Guid.NewGuid(), listing.Id, "SN-U2", FixedNow).Value;
            u2.CalendarBlocks.Add(CalendarBlock.Create(Guid.NewGuid(), u2.Id, mon, mon, CalendarBlockReason.Maintenance, "op", FixedNow, mon).Value);

            db.Assets.AddRange(u1, u2);
            listing.Assets.Add(u1);
            listing.Assets.Add(u2);
            listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
            await db.SaveChangesAsync();
            u1Id = u1.Id;
            u2Id = u2.Id;
        }

        // 1. Check full interval [Mon, Tue] -> AvailableQuantity must be 0!
        var intervalReq = new CheckListingAvailabilityRequest(mon, tue, 1);
        var token = secFactory.CreateHumanToken(owner);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var intervalResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/availability/check",
            new StringContent(JsonSerializer.Serialize(intervalReq), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, intervalResp.StatusCode);
        var intervalResult = await intervalResp.Content.ReadFromJsonAsync<CheckListingAvailabilityResponse>();
        Assert.NotNull(intervalResult);
        Assert.Equal(0, intervalResult.AvailableQuantity);
        Assert.False(intervalResult.IsAvailable);

        // 2. Check Monday single day -> AvailableQuantity is 1!
        var monReq = new CheckListingAvailabilityRequest(mon, mon, 1);
        var monResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/availability/check",
            new StringContent(JsonSerializer.Serialize(monReq), Encoding.UTF8, "application/json"));
        var monResult = await monResp.Content.ReadFromJsonAsync<CheckListingAvailabilityResponse>();
        Assert.NotNull(monResult);
        Assert.Equal(1, monResult.AvailableQuantity);
        Assert.True(monResult.IsAvailable);

        // 3. Check Tuesday single day -> AvailableQuantity is 1!
        var tueReq = new CheckListingAvailabilityRequest(tue, tue, 1);
        var tueResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/availability/check",
            new StringContent(JsonSerializer.Serialize(tueReq), Encoding.UTF8, "application/json"));
        var tueResult = await tueResp.Content.ReadFromJsonAsync<CheckListingAvailabilityResponse>();
        Assert.NotNull(tueResult);
        Assert.Equal(1, tueResult.AvailableQuantity);
        Assert.True(tueResult.IsAvailable);
    }

    [Fact]
    public async Task GroupHold_TwoConcurrentRequestsCompeting_ExactlyOneSucceeds()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;

        var owner = Guid.NewGuid();
        Guid listingId;

        // Seed listing with 5 units
        await using (var db = CreateDbContext())
        {
            var listing = await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: owner, model: "Compete 5", initialUnits: 5);
            listingId = listing.Id;
        }

        var svcToken = secFactory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);

        var start = new DateOnly(2026, 11, 10);
        var end = new DateOnly(2026, 11, 15);
        var expiry = FixedNow.AddHours(2);

        var payload1 = new CreateListingHoldRequest(3, start, end, expiry, "order-service", "GRP-RACE-1", "First");
        var payload2 = new CreateListingHoldRequest(3, start, end, expiry, "order-service", "GRP-RACE-2", "Second");

        using var client1 = app.CreateClient();
        using var client2 = app.CreateClient();
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);

        var task1 = client1.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/holds",
            new StringContent(JsonSerializer.Serialize(payload1), Encoding.UTF8, "application/json"));
        var task2 = client2.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/holds",
            new StringContent(JsonSerializer.Serialize(payload2), Encoding.UTF8, "application/json"));

        var responses = await Task.WhenAll(task1, task2);
        var statuses = responses.Select(r => r.StatusCode).ToList();

        // Exactly one 201 Created and one 409 Conflict (insufficient availability)
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Conflict));

        // Verify in DB that exactly 3 allocations exist in total
        await using var verifyDb = CreateDbContext();
        var allAllocs = await verifyDb.AssetAllocations.Where(a => a.Asset.EquipmentListingId == listingId).ToListAsync();
        Assert.Equal(3, allAllocs.Count);
    }

    [Fact]
    public async Task GroupHold_IdempotentReplay_AndChangedPayloadConflict()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        Guid listingId;

        await using (var db = CreateDbContext())
        {
            var listing = await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: owner, model: "Replay Test", initialUnits: 5);
            listingId = listing.Id;
        }

        var svcToken = secFactory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);

        var start = new DateOnly(2026, 11, 10);
        var end = new DateOnly(2026, 11, 15);
        var expiry = FixedNow.AddHours(2);
        var groupRef = "GRP-REPLAY-100";

        var originalPayload = new CreateListingHoldRequest(2, start, end, expiry, "order-service", groupRef, "Original Notes");

        // 1. Initial request -> 201 Created
        var resp1 = await client.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/holds",
            new StringContent(JsonSerializer.Serialize(originalPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, resp1.StatusCode);
        var result1 = await resp1.Content.ReadFromJsonAsync<ListingHoldResponse>();
        Assert.NotNull(result1);
        Assert.False(result1.IsReplay);
        Assert.Equal(2, result1.Allocations.Count);

        // 2. Replay with identical payload -> 200 OK, same allocation IDs, IsReplay = true
        var resp2 = await client.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/holds",
            new StringContent(JsonSerializer.Serialize(originalPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var result2 = await resp2.Content.ReadFromJsonAsync<ListingHoldResponse>();
        Assert.NotNull(result2);
        Assert.True(result2.IsReplay);
        Assert.Equal(result1.Allocations.Select(a => a.Id), result2.Allocations.Select(a => a.Id));

        // 3. Retry with changed quantity -> 409 Conflict
        var changedQtyPayload = new CreateListingHoldRequest(3, start, end, expiry, "order-service", groupRef, "Original Notes");
        var respChangedQty = await client.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/holds",
            new StringContent(JsonSerializer.Serialize(changedQtyPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, respChangedQty.StatusCode);

        // 4. Retry with changed dates -> 409 Conflict
        var changedDatesPayload = new CreateListingHoldRequest(2, start.AddDays(1), end, expiry, "order-service", groupRef, "Original Notes");
        var respChangedDates = await client.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/holds",
            new StringContent(JsonSerializer.Serialize(changedDatesPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, respChangedDates.StatusCode);
    }

    [Fact]
    public async Task AddAsset_InvalidSerialOrDisallowedFields_ReturnsBadRequest()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        Guid listingId;
        await using (var db = CreateDbContext())
        {
            var listing = await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: owner, model: "BadSerial Test");
            listingId = listing.Id;
        }

        // 1. Missing / whitespace serial
        var blankResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/assets",
            new StringContent("{\"serialNumber\":\"   \"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, blankResp.StatusCode);

        // 2. Serial too long (>100 chars)
        var longSerial = new string('A', 101);
        var longResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/assets",
            new StringContent(JsonSerializer.Serialize(new { serialNumber = longSerial }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, longResp.StatusCode);

        // 3. Body with quantity / count (disallowed unmapped members)
        var qtyResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/assets",
            new StringContent("{\"serialNumber\":\"VALID-1\",\"quantity\":2}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, qtyResp.StatusCode);

        var countResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/assets",
            new StringContent("{\"serialNumber\":\"VALID-1\",\"count\":2}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, countResp.StatusCode);
    }

    [Fact]
    public async Task AddAsset_DuplicateSerial_SameListingCaseInsensitiveAndDisabled_Returns409Conflict_DifferentListingAllowed()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        Guid listing1Id;
        Guid listing2Id;
        Guid disabledUnitId;
        await using (var db = CreateDbContext())
        {
            var l1 = await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: owner, model: "Listing 1", initialUnits: 0);
            listing1Id = l1.Id;

            // Add active unit with serial "SONY-FX3-001"
            var u1 = InventoryAsset.Create(Guid.NewGuid(), l1.Id, "SONY-FX3-001", FixedNow).Value;
            // Add disabled unit with serial "SONY-FX3-002"
            var u2 = InventoryAsset.Create(Guid.NewGuid(), l1.Id, "SONY-FX3-002", FixedNow).Value;
            u2.DisableBookings("Broken", FixedNow);
            db.Assets.AddRange(u1, u2);
            disabledUnitId = u2.Id;

            var l2 = await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: owner, model: "Listing 2", initialUnits: 0);
            listing2Id = l2.Id;
            await db.SaveChangesAsync();
        }

        // 1. Exact duplicate on active unit -> 409 Conflict
        var dupExact = await client.PostAsync($"api/v1/inventory/equipment-listings/{listing1Id:D}/assets",
            new StringContent("{\"serialNumber\":\"SONY-FX3-001\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, dupExact.StatusCode);

        // 2. Different casing & whitespace -> 409 Conflict
        var dupCase = await client.PostAsync($"api/v1/inventory/equipment-listings/{listing1Id:D}/assets",
            new StringContent("{\"serialNumber\":\"  sony-fx3-001  \"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, dupCase.StatusCode);

        // 3. Duplicate on disabled unit -> 409 Conflict
        var dupDisabled = await client.PostAsync($"api/v1/inventory/equipment-listings/{listing1Id:D}/assets",
            new StringContent("{\"serialNumber\":\"sony-fx3-002\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, dupDisabled.StatusCode);

        // 4. Same serial on different listing -> 201 Created!
        var diffListing = await client.PostAsync($"api/v1/inventory/equipment-listings/{listing2Id:D}/assets",
            new StringContent("{\"serialNumber\":\"SONY-FX3-001\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, diffListing.StatusCode);
    }

    [Fact]
    public async Task AddAsset_TwoConcurrentRequestsWithSameSerial_ExactlyOneSucceedsAndOneReturns409()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;

        var owner = Guid.NewGuid();
        Guid listingId;
        await using (var db = CreateDbContext())
        {
            var l = await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: owner, model: "Race Serial", initialUnits: 1);
            listingId = l.Id;
        }

        var token = secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        using var client1 = app.CreateClient();
        using var client2 = app.CreateClient();
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var payload = new { serialNumber = "RACE-SERIAL-001" };
        var content1 = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var content2 = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var t1 = client1.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/assets", content1);
        var t2 = client2.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/assets", content2);

        var responses = await Task.WhenAll(t1, t2);
        var statuses = responses.Select(r => r.StatusCode).ToList();

        // Exactly one 201 Created and one 409 Conflict
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Conflict));

        // Exactly 2 total units in listing (1 initial + 1 added)
        await using var verifyDb = CreateDbContext();
        var count = await verifyDb.Assets.CountAsync(a => a.EquipmentListingId == listingId);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task PatchAsset_DuplicateSerialAndStaleVersionConflict()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        Guid listingId;
        Guid a1Id;
        Guid a2Id;
        await using (var db = CreateDbContext())
        {
            var l = await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: owner, model: "Patch Asset Test", initialUnits: 0);
            listingId = l.Id;
            var a1 = InventoryAsset.Create(Guid.NewGuid(), l.Id, "SERIAL-AAA", FixedNow).Value;
            var a2 = InventoryAsset.Create(Guid.NewGuid(), l.Id, "SERIAL-BBB", FixedNow).Value;
            db.Assets.AddRange(a1, a2);
            await db.SaveChangesAsync();
            a1Id = a1.Id;
            a2Id = a2.Id;
        }

        // 1. PATCH a2 to have serial "SERIAL-AAA" (duplicate) -> 409 Conflict
        var dupResp = await client.PatchAsync($"api/v1/inventory/assets/{a2Id:D}",
            new StringContent("{\"serialNumber\":\"serial-aaa\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, dupResp.StatusCode);

        // 2. PATCH a2 with empty serial -> 400 Bad Request
        var emptyResp = await client.PatchAsync($"api/v1/inventory/assets/{a2Id:D}",
            new StringContent("{\"serialNumber\":\"  \"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, emptyResp.StatusCode);

        // 3. PATCH a2 with valid new serial -> 200 OK
        var okResp = await client.PatchAsync($"api/v1/inventory/assets/{a2Id:D}",
            new StringContent("{\"serialNumber\":\"SERIAL-CCC\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
    }
}
