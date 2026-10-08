using System.Globalization;
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
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
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

public sealed class SingleHoldAndCalendarConsistencyTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _fixture;

    public SingleHoldAndCalendarConsistencyTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class StubCatalogClient : ICatalogServiceClient
    {
        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(
                equipmentTypeId, "EQ-FX3", "Sony FX3 Cinema Line", null, 1, DateTime.UtcNow, null, null)));

        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid brandId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogBrandResponse(brandId, "BR-SONY", "Sony", true)));

        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<CatalogBatchSchemasResponse>> GetEquipmentTypeSchemasBatchAsync(Guid equipmentTypeId, IReadOnlyList<int> versions, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(equipmentTypeId, 1, [new CatalogBatchSchemaItemResponse(1, [])])));
    }

    private sealed class StubMediaClient : IMediaServiceClient
    {
        public Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVerificationInfo(mediaId, "Ready", Guid.NewGuid().ToString("D"))));

        public Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(mediaId, "Public")));
    }

    private (WebApplicationFactory<Program> App, InventorySecurityTestFactory SecFactory) CreateFactory(
        TestTimeProvider? clock = null,
        Func<IServiceProvider, IEquipmentListingRepository>? listingRepoFactory = null,
        Func<IServiceProvider, IAssetAllocationRepository>? allocRepoFactory = null)
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

                if (listingRepoFactory != null)
                {
                    services.RemoveAll<IEquipmentListingRepository>();
                    services.AddScoped(listingRepoFactory);
                }

                if (allocRepoFactory != null)
                {
                    services.RemoveAll<IAssetAllocationRepository>();
                    services.AddScoped(allocRepoFactory);
                }
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

    private async Task<(EquipmentListing Listing, InventoryAsset Asset)> CreateTestListingAndUnitAsync(
        Guid ownerId,
        EquipmentListingStatus status = EquipmentListingStatus.Active)
    {
        await using var db = CreateDbContext();
        var listing = EquipmentListing.Create(
            Guid.NewGuid(),
            ownerId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Sony FX3 Cinema Line",
            1,
            "{}",
            500_000m,
            null,
            null,
            "VND",
            FixedNow).Value;

        if (status == EquipmentListingStatus.Active)
        {
            listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        }
        else if (status == EquipmentListingStatus.Inactive)
        {
            listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
            listing.UpdateStatus(EquipmentListingStatus.Inactive, true, FixedNow);
        }
        // else Draft

        db.EquipmentListings.Add(listing);

        var asset = InventoryAsset.Create(Guid.NewGuid(), listing.Id, "FX3-SHOLD-001", FixedNow).Value;
        db.Assets.Add(asset);

        await db.SaveChangesAsync();
        return (listing, asset);
    }

    [Fact]
    public async Task SingleHold_OnDraftOrInactiveListing_IsRejectedWith409ListingNotActive()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var (draftListing, draftAsset) = await CreateTestListingAndUnitAsync(ownerId, EquipmentListingStatus.Draft);
        var (inactiveListing, inactiveAsset) = await CreateTestListingAndUnitAsync(ownerId, EquipmentListingStatus.Inactive);

        var svcToken = secFactory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);

        var payload = new CreateHoldRequest(
            SourceSystem: "rental_service",
            ExternalReference: "REF-HOLD-DRAFT-01",
            StartDate: new DateOnly(2026, 11, 10),
            EndDate: new DateOnly(2026, 11, 15),
            HoldExpiresAt: FixedNow.AddDays(5),
            Notes: "Hold on draft");

        // 1. Single hold on Draft listing asset -> 409 LISTING_NOT_ACTIVE
        var draftResp = await client.PostAsync($"api/v1/inventory/assets/{draftAsset.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, draftResp.StatusCode);
        var draftBody = await draftResp.Content.ReadAsStringAsync();
        Assert.Contains("LISTING_NOT_ACTIVE", draftBody);

        // 2. Single hold on Inactive listing asset -> 409 LISTING_NOT_ACTIVE
        var inactivePayload = payload with { ExternalReference = "REF-HOLD-INACTIVE-01" };
        var inactiveResp = await client.PostAsync($"api/v1/inventory/assets/{inactiveAsset.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(inactivePayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, inactiveResp.StatusCode);
        var inactiveBody = await inactiveResp.Content.ReadAsStringAsync();
        Assert.Contains("LISTING_NOT_ACTIVE", inactiveBody);

        // 3. Verify ZERO allocations created in DB
        await using var db = CreateDbContext();
        var draftAllocs = await db.AssetAllocations.Where(a => a.AssetId == draftAsset.Id).ToListAsync();
        Assert.Empty(draftAllocs);
        var inactiveAllocs = await db.AssetAllocations.Where(a => a.AssetId == inactiveAsset.Id).ToListAsync();
        Assert.Empty(inactiveAllocs);
    }

    [Fact]
    public async Task SingleHold_ReplayAndAllocate_SucceedsEvenIfListingBecomesInactive()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        // 1. Initially Active listing
        var (listing, asset) = await CreateTestListingAndUnitAsync(ownerId, EquipmentListingStatus.Active);

        var svcToken = secFactory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);

        const string extRef = "REF-HOLD-INACT-REPLAY";
        var payload = new CreateHoldRequest(
            SourceSystem: "rental_service",
            ExternalReference: extRef,
            StartDate: new DateOnly(2026, 11, 10),
            EndDate: new DateOnly(2026, 11, 15),
            HoldExpiresAt: FixedNow.AddDays(5),
            Notes: "Hold created when Active");

        // 2. Create hold while Active -> 201 Created
        var createResp = await client.PostAsync($"api/v1/inventory/assets/{asset.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var holdCreated = await createResp.Content.ReadFromJsonAsync<AllocationResponse>();
        Assert.NotNull(holdCreated);

        // 3. Deactivate listing in DB
        await using (var db = CreateDbContext())
        {
            var l = await db.EquipmentListings.SingleAsync(x => x.Id == listing.Id);
            l.UpdateStatus(EquipmentListingStatus.Inactive, true, FixedNow);
            await db.SaveChangesAsync();
        }

        // 4. Replay of existing hold continues to succeed idempotently (200 OK) even though Listing is Inactive
        var replayResp = await client.PostAsync($"api/v1/inventory/assets/{asset.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, replayResp.StatusCode);

        // 5. Allocate existing hold continues to succeed (200 OK)
        var allocatePayload = new { operationKey = "OP-ALLOCATE-INACT-01" };
        var allocResp = await client.PostAsync($"api/v1/inventory/allocations/{holdCreated.Id:D}/allocate",
            new StringContent(JsonSerializer.Serialize(allocatePayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, allocResp.StatusCode);

        var allocatedRes = await allocResp.Content.ReadFromJsonAsync<AllocationResponse>();
        Assert.NotNull(allocatedRes);
        Assert.Equal("Allocated", allocatedRes.Status);
    }

    private async Task WaitForAdvisoryLockWaiterAsync(Guid listingId, TimeSpan timeout)
    {
        long listingLockKey = AssetLockKey.FromListingId(listingId);

        using var cts = new CancellationTokenSource(timeout);
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync(cts.Token);

        while (!cts.IsCancellationRequested)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT count(*)
                FROM pg_locks
                WHERE locktype = 'advisory'
                  AND ((classid::bigint << 32) | (objid::bigint & 4294967295)) = @key
                  AND NOT granted;
                """, conn);
            cmd.Parameters.AddWithValue("key", listingLockKey);

            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(cts.Token), CultureInfo.InvariantCulture);
            if (count > 0)
            {
                return; // Lock waiter observed!
            }

            await Task.Delay(20, cts.Token);
        }

        throw new TimeoutException($"Hết thời gian chờ quan sát lock waiter cho listing {listingId} trong pg_locks.");
    }

    [Fact]
    public async Task LockCoordination_PatchWinsListingLockFirst_HoldWaitsAndFailsWith409ListingNotActive()
    {
        var ownerId = Guid.NewGuid();
        var (listing, asset) = await CreateTestListingAndUnitAsync(ownerId, EquipmentListingStatus.Active);

        var patchAcquiredLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPatchCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int patchReadCount = 0;

        var (app, secFactory) = CreateFactory(
            listingRepoFactory: sp => new InterceptingListingRepository(
                new EquipmentListingRepository(sp.GetRequiredService<InventoryServiceDbContext>()),
                onGetById: async (l) =>
                {
                    // Call 2 is the read inside ExecuteListingWriteAsync while holding the advisory lock
                    if (Interlocked.Increment(ref patchReadCount) == 2)
                    {
                        patchAcquiredLock.TrySetResult();
                        await allowPatchCommit.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }));

        await using var _ = app;
        await using var __ = secFactory;

        using var clientPatch = app.CreateClient();
        using var clientHold = app.CreateClient();

        var humanToken = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        var svcToken = secFactory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        clientPatch.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", humanToken);
        clientHold.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);

        var patchPayload = new { status = "Inactive" };
        var holdPayload = new CreateHoldRequest(
            SourceSystem: "rental_service",
            ExternalReference: "REF-RACE-PATCH-WINS-01",
            StartDate: new DateOnly(2026, 11, 10),
            EndDate: new DateOnly(2026, 11, 15),
            HoldExpiresAt: FixedNow.AddDays(5));

        // 1. Launch PATCH: acquires Listing lock and pauses inside transaction
        var tPatch = clientPatch.PatchAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}",
            new StringContent(JsonSerializer.Serialize(patchPayload), Encoding.UTF8, "application/json"));

        // Wait until PATCH has acquired the Listing advisory lock in PostgreSQL
        await patchAcquiredLock.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 2. Launch single hold: enters ExecuteAsync and attempts to acquire Listing lock
        var tHold = clientHold.PostAsync($"api/v1/inventory/assets/{asset.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(holdPayload), Encoding.UTF8, "application/json"));

        // 3. Observe in pg_locks that hold connection is WAITING for the Listing advisory lock!
        await WaitForAdvisoryLockWaiterAsync(listing.Id, TimeSpan.FromSeconds(5));

        // 4. Release PATCH to commit its update to Inactive
        allowPatchCommit.TrySetResult();
        var respPatch = await tPatch;
        Assert.Equal(HttpStatusCode.OK, respPatch.StatusCode);

        // 5. Hold unblocks, sees Listing is now Inactive, and fails with 409 LISTING_NOT_ACTIVE
        var respHold = await tHold;
        Assert.Equal(HttpStatusCode.Conflict, respHold.StatusCode);
        var bodyHold = await respHold.Content.ReadAsStringAsync();
        Assert.Contains("LISTING_NOT_ACTIVE", bodyHold);

        // 6. Verify in DB: 0 allocations, 0 transitions, 0 outbox messages for the rejected hold
        await using var db = CreateDbContext();
        var finalListing = await db.EquipmentListings.SingleAsync(x => x.Id == listing.Id);
        Assert.Equal(EquipmentListingStatus.Inactive, finalListing.Status);

        var allocs = await db.AssetAllocations.Where(a => a.AssetId == asset.Id).ToListAsync();
        Assert.Empty(allocs);

        var transitions = await db.AllocationTransitions.Where(t => db.AssetAllocations.Any(a => a.Id == t.AllocationId && a.AssetId == asset.Id)).ToListAsync();
        Assert.Empty(transitions);
        var outbox = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync();
        Assert.Empty(outbox);
    }

    [Fact]
    public async Task LockCoordination_HoldWinsListingLockFirst_PatchWaitsAndBothSucceed()
    {
        var ownerId = Guid.NewGuid();
        var (listing, asset) = await CreateTestListingAndUnitAsync(ownerId, EquipmentListingStatus.Active);

        var holdAcquiredLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowHoldCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (app, secFactory) = CreateFactory(
            allocRepoFactory: sp => new InterceptingAllocationRepository(
                new AssetAllocationRepository(sp.GetRequiredService<InventoryServiceDbContext>()),
                onGetActiveCandidates: async () =>
                {
                    // Inside ExecuteAsync: Hold already acquired Listing lock and Asset lock
                    holdAcquiredLock.TrySetResult();
                    await allowHoldCommit.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }));

        await using var _ = app;
        await using var __ = secFactory;

        using var clientHold = app.CreateClient();
        using var clientPatch = app.CreateClient();

        var svcToken = secFactory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        var humanToken = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        clientHold.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);
        clientPatch.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", humanToken);

        const string extRef = "REF-RACE-HOLD-WINS-01";
        var holdPayload = new CreateHoldRequest(
            SourceSystem: "rental_service",
            ExternalReference: extRef,
            StartDate: new DateOnly(2026, 11, 10),
            EndDate: new DateOnly(2026, 11, 15),
            HoldExpiresAt: FixedNow.AddDays(5));

        var patchPayload = new { status = "Inactive" };

        // 1. Launch single hold: enters ExecuteAsync, acquires Listing lock, and pauses inside operation
        var tHold = clientHold.PostAsync($"api/v1/inventory/assets/{asset.Id:D}/holds",
            new StringContent(JsonSerializer.Serialize(holdPayload), Encoding.UTF8, "application/json"));

        // Wait until Hold has acquired the Listing advisory lock in PostgreSQL
        await holdAcquiredLock.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 2. Launch PATCH: attempts to acquire Listing lock and blocks
        var tPatch = clientPatch.PatchAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}",
            new StringContent(JsonSerializer.Serialize(patchPayload), Encoding.UTF8, "application/json"));

        // 3. Observe in pg_locks that PATCH connection is WAITING for the Listing advisory lock!
        await WaitForAdvisoryLockWaiterAsync(listing.Id, TimeSpan.FromSeconds(5));

        // 4. Release Hold to commit its creation
        allowHoldCommit.TrySetResult();
        var respHold = await tHold;
        Assert.Equal(HttpStatusCode.Created, respHold.StatusCode);

        // 5. PATCH unblocks, acquires Listing lock, updates status to Inactive, and commits
        var respPatch = await tPatch;
        Assert.Equal(HttpStatusCode.OK, respPatch.StatusCode);

        // 6. Verify in DB: Listing is Inactive, but hold was successfully persisted
        await using var db = CreateDbContext();
        var finalListing = await db.EquipmentListings.SingleAsync(x => x.Id == listing.Id);
        Assert.Equal(EquipmentListingStatus.Inactive, finalListing.Status);

        var alloc = await db.AssetAllocations.SingleAsync(a => a.AssetId == asset.Id && a.ExternalReference == extRef);
        Assert.Equal(AllocationStatus.Held, alloc.Status);

        var transitions = await db.AllocationTransitions.Where(t => t.AllocationId == alloc.Id).ToListAsync();
        Assert.Single(transitions);
        Assert.Equal(AllocationOperationType.CreateHold, transitions[0].OperationType);
        var outbox = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync();
        Assert.Single(outbox);
    }

    [Fact]
    public async Task GetListingCalendar_HoldExpiresDuringSnapshotIO_EvaluatesOccupiedFalse()
    {
        var ownerId = Guid.NewGuid();
        var (listing, asset) = await CreateTestListingAndUnitAsync(ownerId, EquipmentListingStatus.Active);

        // Create a hold that will expire exactly during the repository snapshot I/O
        var holdExpiry = FixedNow.AddSeconds(10);
        await using (var db = CreateDbContext())
        {
            var hold = AssetAllocation.CreateHold(
                Guid.NewGuid(),
                asset.Id,
                RentalPeriod.Create(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 12)).Value,
                holdExpiry,
                FixedNow,
                "partner",
                "EXT-HOLD-EXP-MID").Value;
            db.AssetAllocations.Add(hold);
            await db.SaveChangesAsync();
        }

        var clock = new TestTimeProvider(FixedNow);

        // Intercepting repository that advances the clock to AFTER holdExpiry during snapshot I/O
        var (app, secFactory) = CreateFactory(clock, listingRepoFactory: sp =>
            new DelayingListingRepository(
                new EquipmentListingRepository(sp.GetRequiredService<InventoryServiceDbContext>()),
                onGetCalendarSnapshot: () =>
                {
                    // Clock advances 20 seconds during I/O -> hold is now expired when clock is taken post-I/O!
                    clock.Advance(TimeSpan.FromSeconds(20));
                }));

        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var resp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/calendar?from=2026-11-10&to=2026-11-12");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var calendar = await resp.Content.ReadFromJsonAsync<ListingCalendarResponse>();
        Assert.NotNull(calendar);

        // Because calendar service takes evaluationNowUtc AFTER snapshot I/O,
        // it correctly observes the hold as expired (occupied = 0, available = 1)!
        foreach (var day in calendar.Days)
        {
            Assert.Equal(0, day.OccupiedQuantity);
            Assert.Equal(1, day.AvailableQuantity);
        }
    }

    private sealed class DelayingListingRepository : IEquipmentListingRepository
    {
        private readonly IEquipmentListingRepository _inner;
        private readonly Action _onGetCalendarSnapshot;

        public DelayingListingRepository(IEquipmentListingRepository inner, Action onGetCalendarSnapshot)
        {
            _inner = inner;
            _onGetCalendarSnapshot = onGetCalendarSnapshot;
        }

        public async Task<EquipmentListing?> GetCalendarSnapshotAsync(Guid listingId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
        {
            var snapshot = await _inner.GetCalendarSnapshotAsync(listingId, startDate, endDate, cancellationToken);
            _onGetCalendarSnapshot();
            return snapshot;
        }

        public Task<EquipmentListing?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            _inner.GetByIdAsync(id, cancellationToken);

        public Task<EquipmentListing?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            _inner.GetByIdForOwnerAsync(id, ownerUserId, cancellationToken);

        public Task<EquipmentListing?> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            _inner.GetPublicByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<EquipmentListing>> ListForOwnerAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default) =>
            _inner.ListForOwnerAsync(ownerUserId, skip, take, cancellationToken);

        public Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) =>
            _inner.CountForOwnerAsync(ownerUserId, cancellationToken);

        public Task<int> GetTotalQuantityAsync(Guid listingId, CancellationToken cancellationToken = default) =>
            _inner.GetTotalQuantityAsync(listingId, cancellationToken);

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsPublicAsync(AssetSearchFilters filters, CancellationToken cancellationToken = default) =>
            _inner.GetDistinctSchemaVersionsPublicAsync(filters, cancellationToken);

        public Task<PublicEquipmentListingSearchResult> SearchPublicAsync(EquipmentListingSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            _inner.SearchPublicAsync(criteria, cancellationToken);

        public void Add(EquipmentListing listing) => _inner.Add(listing);
        public void Update(EquipmentListing listing) => _inner.Update(listing);
    }

    private sealed class InterceptingListingRepository : IEquipmentListingRepository
    {
        private readonly IEquipmentListingRepository _inner;
        private readonly Func<EquipmentListing?, Task>? _onGetById;

        public InterceptingListingRepository(IEquipmentListingRepository inner, Func<EquipmentListing?, Task>? onGetById = null)
        {
            _inner = inner;
            _onGetById = onGetById;
        }

        public async Task<EquipmentListing?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var res = await _inner.GetByIdAsync(id, cancellationToken);
            if (_onGetById != null)
            {
                await _onGetById(res);
            }
            return res;
        }

        public Task<EquipmentListing?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            _inner.GetByIdForOwnerAsync(id, ownerUserId, cancellationToken);

        public Task<EquipmentListing?> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            _inner.GetPublicByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<EquipmentListing>> ListForOwnerAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default) =>
            _inner.ListForOwnerAsync(ownerUserId, skip, take, cancellationToken);

        public Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) =>
            _inner.CountForOwnerAsync(ownerUserId, cancellationToken);

        public Task<int> GetTotalQuantityAsync(Guid listingId, CancellationToken cancellationToken = default) =>
            _inner.GetTotalQuantityAsync(listingId, cancellationToken);

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsPublicAsync(AssetSearchFilters filters, CancellationToken cancellationToken = default) =>
            _inner.GetDistinctSchemaVersionsPublicAsync(filters, cancellationToken);

        public Task<PublicEquipmentListingSearchResult> SearchPublicAsync(EquipmentListingSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            _inner.SearchPublicAsync(criteria, cancellationToken);

        public Task<EquipmentListing?> GetCalendarSnapshotAsync(Guid listingId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            _inner.GetCalendarSnapshotAsync(listingId, startDate, endDate, cancellationToken);

        public void Add(EquipmentListing listing) => _inner.Add(listing);
        public void Update(EquipmentListing listing) => _inner.Update(listing);
    }

    private sealed class InterceptingAllocationRepository : IAssetAllocationRepository
    {
        private readonly IAssetAllocationRepository _inner;
        private readonly Func<Task>? _onGetActiveCandidates;

        public InterceptingAllocationRepository(IAssetAllocationRepository inner, Func<Task>? onGetActiveCandidates = null)
        {
            _inner = inner;
            _onGetActiveCandidates = onGetActiveCandidates;
        }

        public async Task<IReadOnlyList<AssetAllocation>> GetActiveCandidatesAsync(Guid assetId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
        {
            if (_onGetActiveCandidates != null)
            {
                await _onGetActiveCandidates();
            }
            return await _inner.GetActiveCandidatesAsync(assetId, startDate, endDate, cancellationToken);
        }

        public Task<AssetAllocation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            _inner.GetByIdAsync(id, cancellationToken);

        public Task<AssetAllocation?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            _inner.GetByIdForOwnerAsync(id, ownerUserId, cancellationToken);

        public Task<AssetAllocation?> GetByExternalReferenceAsync(string sourceSystem, string externalReference, CancellationToken cancellationToken = default) =>
            _inner.GetByExternalReferenceAsync(sourceSystem, externalReference, cancellationToken);

        public Task<IReadOnlyList<AssetAllocation>> GetByGroupReferenceAsync(string sourceSystem, string groupReference, CancellationToken cancellationToken = default) =>
            _inner.GetByGroupReferenceAsync(sourceSystem, groupReference, cancellationToken);

        public Task<IReadOnlyList<AssetAllocation>> GetExpiredHoldsAsync(Guid assetId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            _inner.GetExpiredHoldsAsync(assetId, now, cancellationToken);

        public Task<IReadOnlyList<AssetAllocation>> ListByAssetAsync(Guid assetId, int skip, int take, CancellationToken cancellationToken = default) =>
            _inner.ListByAssetAsync(assetId, skip, take, cancellationToken);

        public Task<IReadOnlyList<AssetAllocation>> ListByOwnerAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default) =>
            _inner.ListByOwnerAsync(ownerUserId, skip, take, cancellationToken);

        public void Add(AssetAllocation allocation) => _inner.Add(allocation);
        public void AddRange(IEnumerable<AssetAllocation> allocations) => _inner.AddRange(allocations);
        public void Update(AssetAllocation allocation) => _inner.Update(allocation);
    }
}
