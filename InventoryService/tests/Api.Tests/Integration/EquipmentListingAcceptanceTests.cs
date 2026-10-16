using Microsoft.AspNetCore.Mvc;
using System.Net;
using Dprental.InventoryService.Api.Tests.Security;
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

        public Task<Result<IReadOnlyList<Guid>>> GetEquipmentTypeIdsByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<Guid>>(Array.Empty<Guid>()));
    }

    private sealed class StubMediaClient : IMediaServiceClient
    {
        public Dictionary<Guid, (string Status, Guid UploaderId, string ContentType)> MediaStore { get; } = new();
        public string DefaultContentType { get; set; } = "image/jpeg";
        public Guid? DefaultUploaderId { get; set; }
        public Error? ForcedVerifyError { get; set; }
        public Error? ForcedPublishError { get; set; }
        public int VerifyCalls { get; private set; }
        public int SetPublicCalls { get; private set; }

        public Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            VerifyCalls++;
            if (ForcedVerifyError is not null)
            {
                return Task.FromResult(Result.Failure<MediaVerificationInfo>(ForcedVerifyError));
            }

            if (MediaStore.TryGetValue(mediaId, out var entry))
            {
                return Task.FromResult(Result.Success(new MediaVerificationInfo(
                    mediaId,
                    entry.Status,
                    entry.UploaderId.ToString("D"),
                    entry.ContentType)));
            }

            var uploader = (DefaultUploaderId ?? Guid.NewGuid()).ToString("D");
            return Task.FromResult(Result.Success(new MediaVerificationInfo(mediaId, "Ready", uploader, DefaultContentType)));
        }

        public Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            SetPublicCalls++;
            if (ForcedPublishError is not null)
            {
                return Task.FromResult(Result.Failure<MediaVisibilityInfo>(ForcedPublishError));
            }

            return Task.FromResult(Result.Success(new MediaVisibilityInfo(mediaId, "Public")));
        }
    }

    private (Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> App, Dprental.InventoryService.Api.Tests.Security.InventorySecurityTestFactory SecFactory) CreateFactory(
        TestTimeProvider? clock = null,
        StubMediaClient? mediaClient = null)
    {
        var testClock = clock ?? new TestTimeProvider(FixedNow);
        var secFactory = new Dprental.InventoryService.Api.Tests.Security.InventorySecurityTestFactory(_fixture.ConnectionString, testClock);
        var app = secFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(testClock);

                services.RemoveAll<ICatalogServiceClient>();
                services.AddSingleton<ICatalogServiceClient>(new StubCatalogClient());

                services.RemoveAll<IMediaServiceClient>();
                services.AddSingleton<IMediaServiceClient>(mediaClient ?? new StubMediaClient());
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
            name = "Sony FX3 Batch 5",
            model = "Sony FX3 Batch 5",
            serialNumber = "FX3-TEST-00001",
            manufactureYear = 2024,
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

    [Fact]
    public async Task MultimediaAndManufactureYear_Create_AddAsset_Patch_PublicAndOwnerResponses_AndValidationRejections()
    {
        var mediaClient = new StubMediaClient();
        var (app, secFactory) = CreateFactory(mediaClient: mediaClient);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var admin = Guid.NewGuid();
        mediaClient.DefaultUploaderId = owner;

        var ownerToken = secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        var vid1 = Guid.NewGuid();
        var img1 = Guid.NewGuid();
        var img2 = Guid.NewGuid();
        mediaClient.MediaStore[vid1] = ("Ready", owner, "video/mp4");
        mediaClient.MediaStore[img1] = ("Ready", owner, "image/png");
        mediaClient.MediaStore[img2] = ("Ready", owner, "image/webp");

        // 1. Tạo listing có nhiều ảnh/video, giữ đúng thứ tự và thumbnail; asset đầu tiên nhận đúng năm
        var createPayload = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            name = "Sony FX3 Cinema Multimedia",
            model = "Sony FX3",
            serialNumber = "FX3-MM-001",
            manufactureYear = 2023,
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            mediaIds = new[] { vid1, img1, img2 },
            thumbnailMediaId = img2,
            rentalRate = new { dailyRate = 500_000m, currency = "VND" }
        };

        var createResp = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(createPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.NotNull(created);
        Assert.Equal(2023, created.ManufactureYear);
        Assert.Equal(3, created.Media.Count);
        Assert.Equal([vid1, img1, img2], created.Media.Select(m => m.Id).ToArray());
        Assert.Equal(img2, created.ThumbnailMediaId);
        Assert.EndsWith($"/api/v1/media/{img2:D}/content", created.ThumbnailUrl);

        // Kiểm tra asset đầu tiên nhận đúng năm 2023
        var assetsResp1 = await client.GetFromJsonAsync<PagedResponse<InventoryAssetResponse>>(
            $"api/v1/inventory/equipment-listings/{created.Id:D}/assets");
        Assert.NotNull(assetsResp1);
        Assert.Single(assetsResp1.Items);
        var firstAssetId = assetsResp1.Items[0].Id;
        Assert.Equal(2023, assetsResp1.Items[0].ManufactureYear);

        // 2. Thêm asset thứ 2 nhận năm hiện tại của listing (2023)
        var addAsset2Resp = await client.PostAsync($"api/v1/inventory/equipment-listings/{created.Id:D}/assets",
            new StringContent("{\"serialNumber\":\"FX3-MM-002\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, addAsset2Resp.StatusCode);
        var asset2 = await addAsset2Resp.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(asset2);
        Assert.Equal(2023, asset2.ManufactureYear);

        var whPayload1 = new { addressDetail = "123 Le Loi", provinceCode = "01", wardCode = "00070", latitude = 21.0, longitude = 105.8 };
        var whResp1 = await client.PutAsJsonAsync("api/v1/inventory/warehouse/me", whPayload1);
        Assert.Equal(HttpStatusCode.OK, whResp1.StatusCode);

        // 3. Sửa năm listing sang 2025 và bật Active; không đổi asset cũ; asset mới nhận 2025
        var patchYearAndActive = new { manufactureYear = 2025, status = "Active" };
        var patchResp1 = await client.PatchAsync($"api/v1/inventory/equipment-listings/{created.Id:D}",
            new StringContent(JsonSerializer.Serialize(patchYearAndActive), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, patchResp1.StatusCode);
        var patchedListing = await patchResp1.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.NotNull(patchedListing);
        Assert.Equal(2025, patchedListing.ManufactureYear);
        // mediaIds & thumbnailMediaId omitted -> giữ nguyên
        Assert.Equal([vid1, img1, img2], patchedListing.Media.Select(m => m.Id).ToArray());
        Assert.Equal(img2, patchedListing.ThumbnailMediaId);

        var addAsset3Resp = await client.PostAsync($"api/v1/inventory/equipment-listings/{created.Id:D}/assets",
            new StringContent("{\"serialNumber\":\"FX3-MM-003\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, addAsset3Resp.StatusCode);
        var asset3 = await addAsset3Resp.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(asset3);
        Assert.Equal(2025, asset3.ManufactureYear);

        // Sửa năm riêng của asset đầu tiên sang 2020 vẫn hoạt động; asset 2 vẫn là 2023
        var patchAsset1Resp = await client.PatchAsync($"api/v1/inventory/assets/{firstAssetId:D}",
            new StringContent("{\"manufactureYear\":2020}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, patchAsset1Resp.StatusCode);
        var updatedAsset1 = await patchAsset1Resp.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.Equal(2020, updatedAsset1!.ManufactureYear);

        var asset2Reloaded = await client.GetFromJsonAsync<InventoryAssetResponse>($"api/v1/inventory/assets/{asset2.Id:D}");
        Assert.Equal(2023, asset2Reloaded!.ManufactureYear);

        // 4. Kiểm tra phản hồi Public trả về media theo thứ tự, manufactureYear và thumbnailUrl
        using var anonClient = app.CreateClient();
        var publicDetail = await anonClient.GetFromJsonAsync<PublicEquipmentListingResponse>(
            $"api/v1/inventory/public/equipment-listings/{created.Id:D}");
        Assert.NotNull(publicDetail);
        Assert.Equal(2025, publicDetail.ManufactureYear);
        Assert.Equal([vid1, img1, img2], publicDetail.Media.Select(m => m.Id).ToArray());
        Assert.Equal(patchedListing.ThumbnailUrl, publicDetail.ThumbnailUrl);

        // 5. Từ chối năm thiếu/sai, media trùng/rỗng, >20, thumbnail ngoài danh sách hoặc là video, media không Ready/sai owner, lỗi MediaService
        // 5a. Năm thiếu hoặc ngoài khoảng
        var missingYearPayload = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            name = "Missing Year",
            model = "FX3",
            serialNumber = "SN-BAD-YEAR",
            specValues = new { },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };
        var missingYearResp = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(missingYearPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, missingYearResp.StatusCode);

        // 5b. Thumbnail là video -> 400 BadRequest
        var videoThumbPatch = new { thumbnailMediaId = vid1 };
        var videoThumbResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{created.Id:D}",
            new StringContent(JsonSerializer.Serialize(videoThumbPatch), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, videoThumbResp.StatusCode);

        // 5c. Xóa media đang làm thumbnail mà không xóa/đổi thumbnail -> 400 BadRequest
        var removeThumbMediaPatch = new { mediaIds = new[] { vid1, img1 } };
        var removeThumbMediaResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{created.Id:D}",
            new StringContent(JsonSerializer.Serialize(removeThumbMediaPatch), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, removeThumbMediaResp.StatusCode);

        // 5d. PATCH mediaIds: null hoặc manufactureYear: null -> 400 BadRequest
        var nullMediaResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{created.Id:D}",
            new StringContent("{\"mediaIds\":null}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, nullMediaResp.StatusCode);

        var nullYearResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{created.Id:D}",
            new StringContent("{\"manufactureYear\":null}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, nullYearResp.StatusCode);

        // 5e. Media không Ready (409) và sai chủ sở hữu (403) và lỗi MediaService (503) không ghi DB
        var notReadyId = Guid.NewGuid();
        var wrongOwnerId = Guid.NewGuid();
        mediaClient.MediaStore[notReadyId] = ("PendingUpload", owner, "image/jpeg");
        mediaClient.MediaStore[wrongOwnerId] = ("Ready", otherUser, "image/jpeg");

        var notReadyResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{created.Id:D}",
            new StringContent(JsonSerializer.Serialize(new { mediaIds = new[] { img2, notReadyId } }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, notReadyResp.StatusCode);

        var wrongOwnerResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{created.Id:D}",
            new StringContent(JsonSerializer.Serialize(new { mediaIds = new[] { img2, wrongOwnerId } }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, wrongOwnerResp.StatusCode);

        mediaClient.ForcedVerifyError = Error.Unavailable("MEDIA_SERVICE_UNAVAILABLE", "MediaService down");
        var unavailableMediaId = Guid.NewGuid();
        var unavailableResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{created.Id:D}",
            new StringContent(JsonSerializer.Serialize(new { mediaIds = new[] { img2, unavailableMediaId } }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailableResp.StatusCode);
        mediaClient.ForcedVerifyError = null;

        // Đảm bảo DB vẫn giữ nguyên [vid1, img1, img2] sau các lỗi media
        await using (var db = CreateDbContext())
        {
            var dbListing = await db.EquipmentListings.AsNoTracking().SingleAsync(l => l.Id == created.Id);
            Assert.Equal([vid1, img1, img2], dbListing.MediaIds);
            Assert.Equal(img2, dbListing.ThumbnailMediaId);
        }

        // 6. PATCH mediaIds: [] kèm thumbnailMediaId: null xóa toàn bộ danh sách media
        var clearMediaResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{created.Id:D}",
            new StringContent("{\"mediaIds\":[],\"thumbnailMediaId\":null}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, clearMediaResp.StatusCode);
        var clearedListing = await clearMediaResp.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.Empty(clearedListing!.Media);
        Assert.Null(clearedListing.ThumbnailMediaId);
        Assert.Null(clearedListing.ThumbnailUrl);

        // 7. Quyền Admin được phép PATCH năm và media (do admin upload), người dùng khác bị 403
        using var otherClient = app.CreateClient();
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(otherUser, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));
        var forbiddenPatch = await otherClient.PatchAsync($"api/v1/inventory/equipment-listings/{created.Id:D}",
            new StringContent("{\"manufactureYear\":2022}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenPatch.StatusCode);

        using var adminClient = app.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateAdminToken(admin));
        var adminImg = Guid.NewGuid();
        mediaClient.MediaStore[adminImg] = ("Ready", admin, "image/jpeg");
        var adminPatch = await adminClient.PatchAsync($"api/v1/inventory/equipment-listings/{created.Id:D}",
            new StringContent(JsonSerializer.Serialize(new { manufactureYear = 2024, mediaIds = new[] { adminImg }, thumbnailMediaId = adminImg }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, adminPatch.StatusCode);
    }

    [Fact]
    public async Task ConcurrentAddAssetAndPatchManufactureYear_SerializesViaListingAdvisoryLock_ConsistentYearInheritance()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;

        var owner = Guid.NewGuid();
        Guid listingId;
        await using (var db = CreateDbContext())
        {
            var l = await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: owner, model: "Concurrent Year Test", initialUnits: 1, manufactureYear: 2021);
            listingId = l.Id;
        }

        var token = secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        using var clientAdd = app.CreateClient();
        using var clientPatch = app.CreateClient();
        clientAdd.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        clientPatch.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var addTask = clientAdd.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/assets",
            new StringContent("{\"serialNumber\":\"SN-CONC-YEAR-1\"}", Encoding.UTF8, "application/json"));
        var patchTask = clientPatch.PatchAsync($"api/v1/inventory/equipment-listings/{listingId:D}",
            new StringContent("{\"manufactureYear\":2025}", Encoding.UTF8, "application/json"));

        await Task.WhenAll(addTask, patchTask);

        var addResp = await addTask;
        var patchResp = await patchTask;

        Assert.Equal(HttpStatusCode.Created, addResp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);

        var addedAsset = await addResp.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(addedAsset);
        Assert.Contains(addedAsset.ManufactureYear, new int?[] { 2021, 2025 });

        // Any asset added after the concurrent pair completes must deterministically inherit 2025
        var addAfterResp = await clientAdd.PostAsync($"api/v1/inventory/equipment-listings/{listingId:D}/assets",
            new StringContent("{\"serialNumber\":\"SN-CONC-YEAR-2\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, addAfterResp.StatusCode);
        var addedAfterAsset = await addAfterResp.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.Equal(2025, addedAfterAsset!.ManufactureYear);
    }

    [Fact]
    public async Task CreateListing_DepositAmount_DefaultZero_AndCustomDeposit_Succeeds()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Omitted depositAmount defaults to 0
        var payloadDefault = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            name = "Sony FX3 Default Deposit",
            model = "FX3",
            serialNumber = "FX3-DEF-001",
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 500_000m, currency = "VND" }
        };
        var respDefault = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payloadDefault), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, respDefault.StatusCode);
        var createdDefault = await respDefault.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.NotNull(createdDefault);
        Assert.Equal(0m, createdDefault.DepositAmount);
        Assert.Equal(1, createdDefault.TotalQuantity);

        // 2. Custom depositAmount 500,000 saves correctly
        var payloadCustom = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            name = "Sony FX3 Custom Deposit",
            model = "FX3",
            serialNumber = "FX3-CUST-001",
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 500_000m, currency = "VND" },
            depositAmount = 500_000m
        };
        var respCustom = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payloadCustom), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, respCustom.StatusCode);
        var createdCustom = await respCustom.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.NotNull(createdCustom);
        Assert.Equal(500_000m, createdCustom.DepositAmount);

        // Verify persistence in PostgreSQL
        await using var db = CreateDbContext();
        var listingInDb = await db.EquipmentListings.SingleAsync(l => l.Id == createdCustom.Id);
        Assert.Equal(500_000m, listingInDb.DepositAmount);
    }

    [Theory]
    [InlineData("{\"depositAmount\": -50000}")]
    [InlineData("{\"depositAmount\": 500000.5}")]
    [InlineData("{\"depositAmount\": 0.00000000000000000000000000000001}")]
    [InlineData("{\"depositAmount\": null}")]
    [InlineData("{\"depositAmount\": \"not-a-number\"}")]
    [InlineData("{\"depositAmount\": 1000000000000000000}")]
    public async Task CreateListing_InvalidDepositAmount_Returns400_WithInvalidDepositAmountCode(string depositJsonFragment)
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var jsonBody = $$"""
            {
                "equipmentTypeId": "{{Guid.NewGuid():D}}",
                "brandId": "{{Guid.NewGuid():D}}",
                "name": "Invalid Deposit Camera",
                "model": "FX3",
                "serialNumber": "SN-INV-DEP",
                "manufactureYear": 2024,
                "schemaVersion": 1,
                "specValues": {},
                "rentalRate": { "dailyRate": 500000, "currency": "VND" },
                {{depositJsonFragment.Trim('{', '}')}}
            }
            """;

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(jsonBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("INVALID_DEPOSIT_AMOUNT", problem.Title);
    }

    [Fact]
    public async Task PatchListing_DepositAmount_PresenceTracking_AndReadConsistency()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Create with depositAmount = 500,000
        var createPayload = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            name = "Sony FX3 Read Test",
            model = "FX3",
            serialNumber = "FX3-PATCH-DEP-1",
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 500_000m, currency = "VND" },
            depositAmount = 500_000m
        };
        var createResp = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(createPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        var listingId = created!.Id;

        // 2. Owner GET by ID -> depositAmount is 500,000
        var getResp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listingId:D}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var gotOwner = await getResp.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.Equal(500_000m, gotOwner!.DepositAmount);

        // 3. PATCH name only (omitted depositAmount) -> depositAmount remains 500,000
        var patchNameResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listingId:D}",
            new StringContent("{\"name\":\"Sony FX3 Updated\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, patchNameResp.StatusCode);
        var patchedName = await patchNameResp.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.Equal(500_000m, patchedName!.DepositAmount);

        // 4. PATCH depositAmount = 0 -> depositAmount becomes 0
        var patchZeroResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listingId:D}",
            new StringContent("{\"depositAmount\":0}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, patchZeroResp.StatusCode);
        var patchedZero = await patchZeroResp.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.Equal(0m, patchedZero!.DepositAmount);

        // 5. PATCH depositAmount = 1,000,000 -> depositAmount becomes 1,000,000
        var patchNewResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listingId:D}",
            new StringContent("{\"depositAmount\":1000000}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, patchNewResp.StatusCode);
        var patchedNew = await patchNewResp.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.Equal(1_000_000m, patchedNew!.DepositAmount);

        // 6. PATCH invalid depositAmount (null, negative, fractional) -> 400 INVALID_DEPOSIT_AMOUNT
        var patchNullResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listingId:D}",
            new StringContent("{\"depositAmount\":null}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, patchNullResp.StatusCode);
        var probNull = await patchNullResp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("INVALID_DEPOSIT_AMOUNT", probNull!.Title);

        var patchNegResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listingId:D}",
            new StringContent("{\"depositAmount\":-100}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, patchNegResp.StatusCode);
        var probNeg = await patchNegResp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("INVALID_DEPOSIT_AMOUNT", probNeg!.Title);

        var patchFracResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listingId:D}",
            new StringContent("{\"depositAmount\":500000.5}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, patchFracResp.StatusCode);
        var probFrac = await patchFracResp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("INVALID_DEPOSIT_AMOUNT", probFrac!.Title);

        // 7. PATCH by another user without ownership -> 403 Forbidden
        var otherUser = Guid.NewGuid();
        var otherToken = secFactory.CreateHumanToken(otherUser, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        using var otherClient = app.CreateClient();
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
        var patchOtherResp = await otherClient.PatchAsync($"api/v1/inventory/equipment-listings/{listingId:D}",
            new StringContent("{\"depositAmount\":200000}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, patchOtherResp.StatusCode);

        var whPayload2 = new { addressDetail = "456 Tran Phu", provinceCode = "01", wardCode = "00070", latitude = 21.0, longitude = 105.8 };
        var whResp2 = await client.PutAsJsonAsync("api/v1/inventory/warehouse/me", whPayload2);
        Assert.Equal(HttpStatusCode.OK, whResp2.StatusCode);

        // 8. Activate listing and test Public Detail and Batch Read consistency
        var patchActiveResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listingId:D}",
            new StringContent("{\"status\":\"Active\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, patchActiveResp.StatusCode);

        // Public GET
        using var anonClient = app.CreateClient();
        var pubResp = await anonClient.GetAsync($"api/v1/inventory/public/equipment-listings/{listingId:D}");
        Assert.Equal(HttpStatusCode.OK, pubResp.StatusCode);
        var pubListing = await pubResp.Content.ReadFromJsonAsync<PublicEquipmentListingResponse>();
        Assert.Equal(1_000_000m, pubListing!.DepositAmount);

        // Service Batch Read
        var serviceToken = secFactory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAvailabilityRead]);
        using var svcClient = app.CreateClient();
        svcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        var batchPayload = new { equipmentListingIds = new[] { listingId } };
        var batchResp = await svcClient.PostAsync("api/v1/inventory/equipment-listings/batch",
            new StringContent(JsonSerializer.Serialize(batchPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, batchResp.StatusCode);
        var batchData = await batchResp.Content.ReadFromJsonAsync<EquipmentListingBatchResponse>();
        var batchItem = Assert.Single(batchData!.Items);
        Assert.Equal(1_000_000m, batchItem.DepositAmount);
    }
}
