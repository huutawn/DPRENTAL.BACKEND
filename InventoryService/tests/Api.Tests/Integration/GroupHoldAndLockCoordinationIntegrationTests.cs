using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Application.DTOs.Calendar;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class GroupHoldAndLockCoordinationIntegrationTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _fixture;

    public GroupHoldAndLockCoordinationIntegrationTests(PostgresFixture fixture)
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
        public Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVerificationInfo(mediaId, "Ready", Guid.NewGuid().ToString("D"), "image/jpeg")));

        public Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(mediaId, "Public")));
    }

    private (WebApplicationFactory<Program> App, InventorySecurityTestFactory SecFactory) CreateFactory(TestTimeProvider? clock = null)
    {
        var testClock = clock ?? new TestTimeProvider(FixedNow);
        var secFactory = new InventorySecurityTestFactory(_fixture.ConnectionString, testClock);
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

    private async Task<EquipmentListing> CreateListingWithUnitsAsync(Guid ownerId, int unitCount, string serialPrefix = "FX3-GRP")
    {
        await using var db = CreateDbContext();
        var listing = EquipmentListing.Create(
            Guid.NewGuid(),
            ownerId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Sony FX3 Cinema Line",
            null,
            "Sony FX3 Cinema Line",
            1,
            "{}",
            100_000m,
            null,
            null,
            "VND",
            FixedNow,
            2024).Value;

        if (!await db.Warehouses.AnyAsync(w => w.OwnerUserId == ownerId))
        {
            db.Warehouses.Add(Warehouse.CreateWithAddress(ownerId, "123 Test Street", "01", "Thành phố Hà Nội", "00070", "Phường Hoàn Kiếm", 21.0, 105.8, FixedNow).Value);
        }
        listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        db.EquipmentListings.Add(listing);

        for (int i = 1; i <= unitCount; i++)
        {
            var unit = InventoryAsset.Create(
                Guid.NewGuid(),
                listing.Id,
                $"{serialPrefix}-{i:D4}",
                FixedNow).Value;
            db.Assets.Add(unit);
        }

        await db.SaveChangesAsync();
        return listing;
    }

    [Fact]
    public async Task GroupHold_FaultInjectionOnTransitionOrOutbox_RollsBackAllAllocationsTransitionsAndOutboxAtomically()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateListingWithUnitsAsync(ownerId, 3, "SN-FAULT");

        var svcToken = secFactory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);

        // Inject PostgreSQL trigger on allocation_transitions that fails when inserting transition for second unit in group
        const string groupRef = "GRP-FAULT-INJECT-01";
        await using var triggerConn = new NpgsqlConnection(_fixture.ConnectionString);
        await triggerConn.OpenAsync();

        await using var setupCmd = triggerConn.CreateCommand();
        setupCmd.CommandText = $"""
            CREATE OR REPLACE FUNCTION trg_fn_fail_group_transition()
            RETURNS TRIGGER AS $$
            DECLARE
                v_grp VARCHAR(100);
            BEGIN
                SELECT group_reference INTO v_grp FROM inventory.asset_allocations WHERE id = NEW.allocation_id;
                IF v_grp = '{groupRef}' THEN
                    IF (SELECT COUNT(*) FROM inventory.allocation_transitions at2
                        JOIN inventory.asset_allocations aa2 ON aa2.id = at2.allocation_id
                        WHERE aa2.group_reference = '{groupRef}') >= 1 THEN
                        RAISE EXCEPTION 'Simulated atomic fault injection on second unit transition in group hold!';
                    END IF;
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS trg_test_fail_group_transition ON inventory.allocation_transitions;
            CREATE TRIGGER trg_test_fail_group_transition
                BEFORE INSERT ON inventory.allocation_transitions
                FOR EACH ROW EXECUTE FUNCTION trg_fn_fail_group_transition();
        """;
        await setupCmd.ExecuteNonQueryAsync();

        try
        {
            var payload = new CreateListingHoldRequest(
                SourceSystem: "rental_service",
                GroupReference: groupRef,
                Quantity: 2,
                StartDate: new DateOnly(2026, 11, 10),
                EndDate: new DateOnly(2026, 11, 15),
                HoldExpiresAt: FixedNow.AddDays(10),
                Notes: "Atomic fault test");

            var response = await client.PostAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/holds",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            // Assert that ZERO allocations and ZERO transitions exist for this group
            await using var verifyDb = CreateDbContext();
            var allocationsCount = await verifyDb.AssetAllocations.CountAsync(a => a.GroupReference == groupRef);
            Assert.Equal(0, allocationsCount);

            var transitionsCount = await verifyDb.AllocationTransitions
                .CountAsync(t => verifyDb.AssetAllocations.Any(a => a.Id == t.AllocationId && a.GroupReference == groupRef));
            Assert.Equal(0, transitionsCount);

            // Verify all 3 units remain available
            var units = await verifyDb.Assets.Where(a => a.EquipmentListingId == listing.Id).ToListAsync();
            Assert.Equal(3, units.Count);
        }
        finally
        {
            await using var cleanupCmd = triggerConn.CreateCommand();
            cleanupCmd.CommandText = "DROP TRIGGER IF EXISTS trg_test_fail_group_transition ON inventory.allocation_transitions; DROP FUNCTION IF EXISTS trg_fn_fail_group_transition();";
            await cleanupCmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task LockCoordination_GroupHold_RacesWithSingleHold_DeterministicLockOrder()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;

        var ownerId = Guid.NewGuid();
        // Listing with exactly 1 unit: both requests will compete for this single unit
        var listing = await CreateListingWithUnitsAsync(ownerId, 1, "SN-LOCK-SINGLE");
        InventoryAsset unit;
        await using (var db = CreateDbContext())
        {
            unit = await db.Assets.SingleAsync(a => a.EquipmentListingId == listing.Id);
        }

        var svcToken = secFactory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);

        using var client1 = app.CreateClient();
        using var client2 = app.CreateClient();
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);

        var start = new DateOnly(2026, 11, 10);
        var end = new DateOnly(2026, 11, 15);
        var expiry = FixedNow.AddDays(5);

        var groupPayload = new CreateListingHoldRequest(
            SourceSystem: "rental_service",
            GroupReference: "GRP-RACE-SINGLE-01",
            Quantity: 1,
            StartDate: start,
            EndDate: end,
            HoldExpiresAt: expiry,
            Notes: "Group competing for unit");

        var singlePayload = new CreateHoldRequest(
            SourceSystem: "rental_service",
            ExternalReference: "EXT-RACE-SINGLE-01",
            StartDate: start,
            EndDate: end,
            HoldExpiresAt: expiry,
            Notes: "Single hold competing for unit");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Launch both requests simultaneously
        var tGroup = client1.PostAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(groupPayload), Encoding.UTF8, "application/json"), cts.Token);

        var tSingle = client2.PostAsync($"api/v1/inventory/assets/{unit.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(singlePayload), Encoding.UTF8, "application/json"), cts.Token);

        await Task.WhenAll(tGroup, tSingle);

        var respGroup = await tGroup;
        var respSingle = await tSingle;

        // Exactly one MUST succeed with 201 Created, and the other MUST be rejected with 409 Conflict
        var statuses = new[] { respGroup.StatusCode, respSingle.StatusCode };
        Assert.Contains(HttpStatusCode.Created, statuses);
        Assert.Contains(HttpStatusCode.Conflict, statuses);

        // Verify in DB that exactly 1 allocation was saved
        await using var verifyDb = CreateDbContext();
        var allocs = await verifyDb.AssetAllocations.Where(a => a.AssetId == unit.Id).ToListAsync();
        Assert.Single(allocs);
    }

    [Fact]
    public async Task LockCoordination_GroupHold_RacesWithCalendarBlock_DeterministicLockOrder()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;

        var ownerId = Guid.NewGuid();
        // Listing with exactly 1 unit
        var listing = await CreateListingWithUnitsAsync(ownerId, 1, "SN-LOCK-BLOCK");
        InventoryAsset unit;
        await using (var db = CreateDbContext())
        {
            unit = await db.Assets.SingleAsync(a => a.EquipmentListingId == listing.Id);
        }

        var svcToken = secFactory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        var humanToken = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);

        using var clientGroup = app.CreateClient();
        using var clientBlock = app.CreateClient();
        clientGroup.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);
        clientBlock.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", humanToken);

        var start = new DateOnly(2026, 11, 10);
        var end = new DateOnly(2026, 11, 15);
        var expiry = FixedNow.AddDays(5);

        var groupPayload = new CreateListingHoldRequest(
            SourceSystem: "rental_service",
            GroupReference: "GRP-RACE-BLOCK-01",
            Quantity: 1,
            StartDate: start,
            EndDate: end,
            HoldExpiresAt: expiry);

        var blockPayload = new CreateCalendarBlockRequest
        {
            StartDate = start,
            EndDate = end,
            Reason = CalendarBlockReason.Maintenance,
            Note = "Maintenance block racing with group hold"
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var tGroup = clientGroup.PostAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(groupPayload), Encoding.UTF8, "application/json"), cts.Token);

        var tBlock = clientBlock.PostAsync($"api/v1/inventory/assets/{unit.Id:D}/calendar-blocks",
            new StringContent(JsonSerializer.Serialize(blockPayload), Encoding.UTF8, "application/json"), cts.Token);

        await Task.WhenAll(tGroup, tBlock);

        var respGroup = await tGroup;
        var respBlock = await tBlock;

        // Exactly one gets 201 Created, the other gets Conflict (409)
        var statuses = new[] { respGroup.StatusCode, respBlock.StatusCode };
        Assert.Contains(HttpStatusCode.Created, statuses);
        Assert.Contains(HttpStatusCode.Conflict, statuses);

        // Verify DB integrity: either 1 allocation and 0 blocks, OR 0 allocations and 1 block
        await using var verifyDb = CreateDbContext();
        var allocCount = await verifyDb.AssetAllocations.CountAsync(a => a.AssetId == unit.Id);
        var blockCount = await verifyDb.CalendarBlocks.CountAsync(b => b.AssetId == unit.Id);

        Assert.True((allocCount == 1 && blockCount == 0) || (allocCount == 0 && blockCount == 1));
    }

    [Fact]
    public async Task LockCoordination_GroupHold_RacesWithDisableAsset_DeterministicLockOrder()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;

        var ownerId = Guid.NewGuid();
        // Listing with exactly 1 unit
        var listing = await CreateListingWithUnitsAsync(ownerId, 1, "SN-LOCK-DIS");
        InventoryAsset unit;
        await using (var db = CreateDbContext())
        {
            unit = await db.Assets.SingleAsync(a => a.EquipmentListingId == listing.Id);
        }

        var svcToken = secFactory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        var humanToken = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);

        using var clientGroup = app.CreateClient();
        using var clientDisable = app.CreateClient();
        clientGroup.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);
        clientDisable.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", humanToken);

        var start = new DateOnly(2026, 11, 10);
        var end = new DateOnly(2026, 11, 15);
        var expiry = FixedNow.AddDays(5);

        var groupPayload = new CreateListingHoldRequest(
            SourceSystem: "rental_service",
            GroupReference: "GRP-RACE-DIS-01",
            Quantity: 1,
            StartDate: start,
            EndDate: end,
            HoldExpiresAt: expiry);

        var patchPayload = new { acceptingBookings = false, reason = "Disabled during race test" };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var tGroup = clientGroup.PostAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(groupPayload), Encoding.UTF8, "application/json"), cts.Token);

        var tDisable = clientDisable.PatchAsync($"api/v1/inventory/assets/{unit.Id:D}/accepting-bookings",
            new StringContent(JsonSerializer.Serialize(patchPayload), Encoding.UTF8, "application/json"), cts.Token);

        await Task.WhenAll(tGroup, tDisable);

        var respGroup = await tGroup;
        var respDisable = await tDisable;

        // Disable succeeds with 200 OK
        Assert.Equal(HttpStatusCode.OK, respDisable.StatusCode);

        // Group hold either succeeds (if it won lock before disable) or fails with 409 INSUFFICIENT_AVAILABILITY
        Assert.True(respGroup.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict);

        await using var verifyDb = CreateDbContext();
        var reloadedUnit = await verifyDb.Assets.SingleAsync(a => a.Id == unit.Id);
        Assert.False(reloadedUnit.AcceptingBookings);
    }

    [Fact]
    public async Task GroupHold_ConcurrentRetry_SameKey_OnlyOneGroupCreated_BothReturnIdenticalResponses()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;

        var ownerId = Guid.NewGuid();
        var listing = await CreateListingWithUnitsAsync(ownerId, 3, "SN-IDEM-CONC");

        var svcToken = secFactory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);

        using var client1 = app.CreateClient();
        using var client2 = app.CreateClient();
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);

        const string groupRef = "GRP-IDEM-CONC-01";
        var payload = new CreateListingHoldRequest(
            SourceSystem: "rental_service",
            GroupReference: groupRef,
            Quantity: 2,
            StartDate: new DateOnly(2026, 11, 10),
            EndDate: new DateOnly(2026, 11, 15),
            HoldExpiresAt: FixedNow.AddDays(5),
            Notes: "Concurrent idempotent retry test");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var t1 = client1.PostAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), cts.Token);

        var t2 = client2.PostAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), cts.Token);

        await Task.WhenAll(t1, t2);

        var r1 = await t1;
        var r2 = await t2;

        Assert.True(r1.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK);
        Assert.True(r2.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK);

        var res1 = await r1.Content.ReadFromJsonAsync<ListingHoldResponse>();
        var res2 = await r2.Content.ReadFromJsonAsync<ListingHoldResponse>();

        Assert.NotNull(res1);
        Assert.NotNull(res2);
        Assert.Equal(res1.ListingId, res2.ListingId);
        Assert.Equal(res1.GroupReference, res2.GroupReference);
        Assert.Equal(2, res1.Quantity);
        Assert.Equal(2, res2.Quantity);

        // Both responses refer to the exact same allocations
        var ids1 = res1.Allocations.Select(a => a.Id).OrderBy(x => x).ToList();
        var ids2 = res2.Allocations.Select(a => a.Id).OrderBy(x => x).ToList();
        Assert.Equal(ids1, ids2);

        // Exactly 2 allocations created in database!
        await using var verifyDb = CreateDbContext();
        var allocCount = await verifyDb.AssetAllocations.CountAsync(a => a.GroupReference == groupRef);
        Assert.Equal(2, allocCount);
    }

    [Fact]
    public async Task GroupHold_Replay_AfterExpiryOrRelease_ReturnsConflictHoldExpired()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateListingWithUnitsAsync(ownerId, 2, "SN-IDEM-EXP");

        var svcToken = secFactory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);

        const string groupRef = "GRP-IDEM-EXP-01";
        var payload = new CreateListingHoldRequest(
            SourceSystem: "rental_service",
            GroupReference: groupRef,
            Quantity: 1,
            StartDate: new DateOnly(2026, 11, 10),
            EndDate: new DateOnly(2026, 11, 15),
            HoldExpiresAt: FixedNow.AddDays(5),
            Notes: "Hold to expire");

        // 1. Initial hold created -> 201 Created
        var initialResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, initialResp.StatusCode);

        // 2. Expire the hold in database
        await using (var db = CreateDbContext())
        {
            var hold = await db.AssetAllocations.Include(a => a.Transitions).SingleAsync(a => a.GroupReference == groupRef);
            hold.ExpireHold(FixedNow.AddDays(6), "system");
            await db.SaveChangesAsync();
        }

        // 3. Replay with same key -> 409 Conflict HOLD_EXPIRED
        var replayResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, replayResp.StatusCode);
        var body = await replayResp.Content.ReadAsStringAsync();
        Assert.Contains("HOLD_EXPIRED", body);

        // Verify no extra allocation created
        await using (var db = CreateDbContext())
        {
            var allocs = await db.AssetAllocations.Where(a => a.GroupReference == groupRef).ToListAsync();
            Assert.Single(allocs);
            Assert.Equal(AllocationStatus.Released, allocs[0].Status);
        }
    }

    [Fact]
    public async Task GroupHold_Replay_ChangedPayload_ReturnsConflictExternalReferenceReused()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateListingWithUnitsAsync(ownerId, 3, "SN-IDEM-CHG");

        var svcToken = secFactory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);

        const string groupRef = "GRP-IDEM-CHG-01";
        var payload = new CreateListingHoldRequest(
            SourceSystem: "rental_service",
            GroupReference: groupRef,
            Quantity: 1,
            StartDate: new DateOnly(2026, 11, 10),
            EndDate: new DateOnly(2026, 11, 15),
            HoldExpiresAt: FixedNow.AddDays(5));

        var initResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, initResp.StatusCode);

        // Replay with different Quantity (2 instead of 1)
        var changedPayload = payload with { Quantity = 2 };
        var chgResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(changedPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, chgResp.StatusCode);
        var body = await chgResp.Content.ReadAsStringAsync();
        Assert.Contains("EXTERNAL_REFERENCE_REUSED", body);
    }

    [Fact]
    public async Task GroupHold_Replay_DifferentListing_ReturnsConflictExternalReferenceReused()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing1 = await CreateListingWithUnitsAsync(ownerId, 2, "SN-IDEM-L1");
        var listing2 = await CreateListingWithUnitsAsync(ownerId, 2, "SN-IDEM-L2");

        var svcToken = secFactory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);

        const string groupRef = "GRP-IDEM-LST-01";
        var payload = new CreateListingHoldRequest(
            SourceSystem: "rental_service",
            GroupReference: groupRef,
            Quantity: 1,
            StartDate: new DateOnly(2026, 11, 10),
            EndDate: new DateOnly(2026, 11, 15),
            HoldExpiresAt: FixedNow.AddDays(5));

        var initResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listing1.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, initResp.StatusCode);

        // Replay same key against listing2
        var diffListingResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listing2.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, diffListingResp.StatusCode);
        var body = await diffListingResp.Content.ReadAsStringAsync();
        Assert.Contains("EXTERNAL_REFERENCE_REUSED", body);
    }
}
