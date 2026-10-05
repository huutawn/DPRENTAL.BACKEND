using System.Globalization;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Application.DTOs.Calendar;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

/// <summary>
/// Kiểm thử đồng thời (Concurrency), cạnh tranh khóa pg_advisory_xact_lock và tính lũy kế (Idempotency)
/// của Hold &amp; Allocation trên cơ sở dữ liệu PostgreSQL 17 thật theo DPR-151.
/// </summary>
public sealed class PostgresAllocationConcurrencyTests : IClassFixture<PostgresFixture>
{
    private readonly string _connectionString;
    private static readonly DateTimeOffset InitialNow = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly InitialToday = BusinessDateTime.ToVietnamDate(InitialNow);

    public PostgresAllocationConcurrencyTests(PostgresFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
    }

    private ServiceProvider BuildServiceProvider(
        TimeProvider? timeProvider = null,
        Func<IServiceProvider, IAssetAllocationRepository, IAssetAllocationRepository>? decorateAllocations = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInventoryServiceDomainRepositories(_connectionString);
        services.AddSingleton(timeProvider ?? new TestTimeProvider(InitialNow));
        if (decorateAllocations is not null)
        {
            var descriptor = services.Single(d => d.ServiceType == typeof(IAssetAllocationRepository));
            services.Remove(descriptor);
            services.AddScoped<IAssetAllocationRepository>(sp =>
            {
                var original = ActivatorUtilities.CreateInstance<AssetAllocationRepository>(sp);
                return decorateAllocations(sp, original);
            });
        }
        services.AddScoped<IInventoryAllocationService, InventoryAllocationService>();
        services.AddScoped<ICalendarService, CalendarService>();
        return services.BuildServiceProvider();
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_connectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    private async Task<InventoryAsset> SeedAssetAsync(Guid ownerUserId, bool acceptingBookings = true)
    {
        await using var db = CreateDbContext();
        var asset = InventoryAsset.Create(
            Guid.NewGuid(),
            ownerUserId,
            Guid.NewGuid(),
            "Sony FX3",
            1,
            "{}",
            InitialNow).Value;

        if (!acceptingBookings)
        {
            asset.DisableBookings("Suspended for test", InitialNow);
        }

        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset;
    }

    private async Task WaitForAdvisoryLockWaiterAsync(Guid assetId, TimeSpan timeout)
    {
        long lockKey = AssetLockKey.FromAssetId(assetId);
        using var cts = new CancellationTokenSource(timeout);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cts.Token);

        while (!cts.IsCancellationRequested)
        {
            await using var cmd = new NpgsqlCommand(@"
                SELECT count(*) 
                FROM pg_locks 
                WHERE locktype = 'advisory'
                  AND ((classid::bigint << 32) | (objid::bigint & 4294967295)) = @key
                  AND NOT granted;", conn);
            cmd.Parameters.AddWithValue("key", lockKey);

            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(cts.Token), CultureInfo.InvariantCulture);
            if (count > 0)
            {
                return;
            }

            await Task.Delay(20, cts.Token);
        }

        throw new TimeoutException($"Hết thời gian chờ quan sát lock waiter cho asset {assetId} (key: {lockKey}) trong pg_locks.");
    }

    private sealed class ServiceCallerContext : IInventoryCallerContext
    {
        public bool IsAuthenticated => true;
        public bool IsService => true;
        public bool IsAdmin => false;
        public Guid? UserId => null;
        public IReadOnlySet<string> Permissions => new HashSet<string>();
        public IReadOnlySet<string> Scopes => new HashSet<string> { InventoryAccessEvaluator.ScopeAllocationWrite };
        public IReadOnlySet<string> Roles => new HashSet<string>();
        public Guid? OrganizationId => null;
    }

    private sealed class HumanOwnerCallerContext(Guid ownerUserId) : IInventoryCallerContext
    {
        public bool IsAuthenticated => true;
        public bool IsService => false;
        public bool IsAdmin => false;
        public Guid? UserId => ownerUserId;
        public IReadOnlySet<string> Permissions => new HashSet<string>();
        public IReadOnlySet<string> Scopes => new HashSet<string>();
        public IReadOnlySet<string> Roles => new HashSet<string>();
        public Guid? OrganizationId => null;
    }

    private sealed class InterceptingWriteTransaction(
        IInventoryWriteTransaction inner,
        Func<IInventoryWriteContext, CancellationToken, Task>? onAfterLockAcquired = null) : IInventoryWriteTransaction
    {
        public Task<Result<T>> ExecuteAsync<T>(
            Guid assetId,
            Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
            CancellationToken cancellationToken = default)
        {
            return inner.ExecuteAsync<T>(assetId, async (ctx, ct) =>
            {
                if (onAfterLockAcquired is not null)
                {
                    await onAfterLockAcquired(ctx, ct);
                }
                return await operation(ctx, ct);
            }, cancellationToken);
        }
    }

    private sealed class InterceptingAllocationRepository(
        IAssetAllocationRepository inner,
        Action? onCall = null) : IAssetAllocationRepository
    {
        public int GetByExternalReferenceCallCount { get; private set; }
        public Func<string, string, Task>? OnAfterGetByExternalReferenceAsync { get; set; }

        public async Task<AssetAllocation?> GetByExternalReferenceAsync(string sourceSystem, string externalReference, CancellationToken cancellationToken = default)
        {
            GetByExternalReferenceCallCount++;
            onCall?.Invoke();
            var result = await inner.GetByExternalReferenceAsync(sourceSystem, externalReference, cancellationToken);
            if (OnAfterGetByExternalReferenceAsync is not null)
            {
                await OnAfterGetByExternalReferenceAsync(sourceSystem, externalReference);
            }
            return result;
        }

        public Task<AssetAllocation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<AssetAllocation?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            inner.GetByIdForOwnerAsync(id, ownerUserId, cancellationToken);

        public Task<IReadOnlyList<AssetAllocation>> GetActiveCandidatesAsync(Guid assetId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            inner.GetActiveCandidatesAsync(assetId, startDate, endDate, cancellationToken);

        public Task<IReadOnlyList<AssetAllocation>> GetExpiredHoldsAsync(Guid assetId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            inner.GetExpiredHoldsAsync(assetId, now, cancellationToken);

        public Task<IReadOnlyList<AssetAllocation>> ListByAssetAsync(Guid assetId, int skip, int take, CancellationToken cancellationToken = default) =>
            inner.ListByAssetAsync(assetId, skip, take, cancellationToken);

        public Task<IReadOnlyList<AssetAllocation>> ListByOwnerAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default) =>
            inner.ListByOwnerAsync(ownerUserId, skip, take, cancellationToken);

        public void Add(AssetAllocation allocation) => inner.Add(allocation);
        public void Update(AssetAllocation allocation) => inner.Update(allocation);
    }

    [Fact]
    public async Task Hold_Hold_Overlap_OnlyOneSucceeds_AndWaiterObservedInPgLocks()
    {
        var clock = new TestTimeProvider(InitialNow);
        using var sp1 = BuildServiceProvider(clock);
        using var sp2 = BuildServiceProvider(clock);

        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);
        var caller = new ServiceCallerContext();

        var hold1EnteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowHold1ToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var baseTx1 = sp1.GetRequiredService<IInventoryWriteTransaction>();
        var interceptingTx1 = new InterceptingWriteTransaction(baseTx1, async (ctx, ct) =>
        {
            hold1EnteredLock.TrySetResult(true);
            await allowHold1ToProceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        });

        var allocationRepo1 = sp1.GetRequiredService<IAssetAllocationRepository>();
        var service1 = new InventoryAllocationService(allocationRepo1, interceptingTx1, clock);
        var service2 = sp2.GetRequiredService<IInventoryAllocationService>();

        var period = new RentalPeriod(InitialToday.AddDays(5), InitialToday.AddDays(10));
        var req1 = new CreateHoldRequest("rental-sys", "REF-HH-1", period.StartDate, period.EndDate, InitialNow.AddHours(2));
        var req2 = new CreateHoldRequest("rental-sys", "REF-HH-2", period.StartDate.AddDays(2), period.EndDate.AddDays(2), InitialNow.AddHours(2));

        Task<Result<CreateHoldResult>>? task1 = null;
        Task<Result<CreateHoldResult>>? task2 = null;

        try
        {
            task1 = Task.Run(() => service1.CreateHoldAsync(asset.Id, req1, caller));
            await hold1EnteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

            task2 = Task.Run(() => service2.CreateHoldAsync(asset.Id, req2, caller));
            await WaitForAdvisoryLockWaiterAsync(asset.Id, TimeSpan.FromSeconds(15));

            allowHold1ToProceed.TrySetResult(true);

            var res1 = await task1.WaitAsync(TimeSpan.FromSeconds(15));
            var res2 = await task2.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(res1.IsSuccess);
            Assert.False(res1.Value.IsReplay);
            Assert.True(res2.IsFailure);
            Assert.Equal("ASSET_UNAVAILABLE", res2.Error.Code);

            await using var db = CreateDbContext();
            var allocs = await db.AssetAllocations.Where(a => a.AssetId == asset.Id).ToListAsync();
            Assert.Single(allocs);
            Assert.Equal("REF-HH-1", allocs[0].ExternalReference);

            var outboxList = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync();
            Assert.Single(outboxList);
        }
        finally
        {
            hold1EnteredLock.TrySetResult(true);
            allowHold1ToProceed.TrySetResult(true);
            if (task1 is not null) await Task.WhenAny(task1, Task.Delay(1000));
            if (task2 is not null) await Task.WhenAny(task2, Task.Delay(1000));
        }
    }

    [Fact]
    public async Task Hold_Block_BothLockOrders_EnforcesAdvisoryLockAndOverlapRejection()
    {
        var clock = new TestTimeProvider(InitialNow);

        // Thứ tự A: Hold thắng lock trước, Block chờ sau
        {
            using var sp1 = BuildServiceProvider(clock);
            using var sp2 = BuildServiceProvider(clock);

            var ownerId = Guid.NewGuid();
            var asset = await SeedAssetAsync(ownerId);
            var serviceCaller = new ServiceCallerContext();
            var humanCaller = new HumanOwnerCallerContext(ownerId);

            var holdEnteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowHoldToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var baseTx1 = sp1.GetRequiredService<IInventoryWriteTransaction>();
            var interceptingTx1 = new InterceptingWriteTransaction(baseTx1, async (ctx, ct) =>
            {
                holdEnteredLock.TrySetResult(true);
                await allowHoldToProceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            });

            var service1 = new InventoryAllocationService(sp1.GetRequiredService<IAssetAllocationRepository>(), interceptingTx1, clock);
            var calendarService2 = sp2.GetRequiredService<ICalendarService>();

            var period = new RentalPeriod(InitialToday.AddDays(15), InitialToday.AddDays(18));
            var holdReq = new CreateHoldRequest("rental-sys", "REF-HB-HOLD", period.StartDate, period.EndDate, InitialNow.AddHours(2));
            var blockReq = new CreateCalendarBlockRequest { StartDate = period.StartDate, EndDate = period.EndDate, Reason = CalendarBlockReason.Maintenance };

            Task<Result<CreateHoldResult>>? holdTask = null;
            Task<Result<CalendarBlockResponse>>? blockTask = null;

            try
            {
                holdTask = Task.Run(() => service1.CreateHoldAsync(asset.Id, holdReq, serviceCaller));
                await holdEnteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

                blockTask = Task.Run(() => calendarService2.CreateCalendarBlockAsync(asset.Id, blockReq, humanCaller));
                await WaitForAdvisoryLockWaiterAsync(asset.Id, TimeSpan.FromSeconds(15));

                allowHoldToProceed.TrySetResult(true);

                var holdRes = await holdTask.WaitAsync(TimeSpan.FromSeconds(15));
                var blockRes = await blockTask.WaitAsync(TimeSpan.FromSeconds(15));

                Assert.True(holdRes.IsSuccess);
                Assert.True(blockRes.IsFailure);
                Assert.Equal("ASSET_UNAVAILABLE", blockRes.Error.Code);
            }
            finally
            {
                holdEnteredLock.TrySetResult(true);
                allowHoldToProceed.TrySetResult(true);
                if (holdTask is not null) await Task.WhenAny(holdTask, Task.Delay(1000));
                if (blockTask is not null) await Task.WhenAny(blockTask, Task.Delay(1000));
            }
        }

        // Thứ tự B: Block thắng lock trước, Hold chờ sau
        {
            using var sp1 = BuildServiceProvider(clock);
            using var sp2 = BuildServiceProvider(clock);

            var ownerId = Guid.NewGuid();
            var asset = await SeedAssetAsync(ownerId);
            var serviceCaller = new ServiceCallerContext();
            var humanCaller = new HumanOwnerCallerContext(ownerId);

            var blockEnteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowBlockToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var baseTx1 = sp1.GetRequiredService<IInventoryWriteTransaction>();
            var interceptingTx1 = new InterceptingWriteTransaction(baseTx1, async (ctx, ct) =>
            {
                blockEnteredLock.TrySetResult(true);
                await allowBlockToProceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            });

            var calendarService1 = new CalendarService(sp1.GetRequiredService<IInventoryAssetRepository>(), interceptingTx1, clock);
            var allocationService2 = sp2.GetRequiredService<IInventoryAllocationService>();

            var period = new RentalPeriod(InitialToday.AddDays(25), InitialToday.AddDays(28));
            var blockReq = new CreateCalendarBlockRequest { StartDate = period.StartDate, EndDate = period.EndDate, Reason = CalendarBlockReason.Inspection };
            var holdReq = new CreateHoldRequest("rental-sys", "REF-BH-HOLD", period.StartDate, period.EndDate, InitialNow.AddHours(2));

            Task<Result<CalendarBlockResponse>>? blockTask = null;
            Task<Result<CreateHoldResult>>? holdTask = null;

            try
            {
                blockTask = Task.Run(() => calendarService1.CreateCalendarBlockAsync(asset.Id, blockReq, humanCaller));
                await blockEnteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

                holdTask = Task.Run(() => allocationService2.CreateHoldAsync(asset.Id, holdReq, serviceCaller));
                await WaitForAdvisoryLockWaiterAsync(asset.Id, TimeSpan.FromSeconds(15));

                allowBlockToProceed.TrySetResult(true);

                var blockRes = await blockTask.WaitAsync(TimeSpan.FromSeconds(15));
                var holdRes = await holdTask.WaitAsync(TimeSpan.FromSeconds(15));

                Assert.True(blockRes.IsSuccess);
                Assert.True(holdRes.IsFailure);
                Assert.Equal("ASSET_BLOCKED", holdRes.Error.Code);
            }
            finally
            {
                blockEnteredLock.TrySetResult(true);
                allowBlockToProceed.TrySetResult(true);
                if (blockTask is not null) await Task.WhenAny(blockTask, Task.Delay(1000));
                if (holdTask is not null) await Task.WhenAny(holdTask, Task.Delay(1000));
            }
        }
    }

    [Fact]
    public async Task NonOverlap_SameAsset_BothSucceed()
    {
        var clock = new TestTimeProvider(InitialNow);
        using var sp1 = BuildServiceProvider(clock);
        using var sp2 = BuildServiceProvider(clock);

        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);
        var caller = new ServiceCallerContext();

        var hold1EnteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowHold1ToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var baseTx1 = sp1.GetRequiredService<IInventoryWriteTransaction>();
        var interceptingTx1 = new InterceptingWriteTransaction(baseTx1, async (ctx, ct) =>
        {
            hold1EnteredLock.TrySetResult(true);
            await allowHold1ToProceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        });

        var service1 = new InventoryAllocationService(sp1.GetRequiredService<IAssetAllocationRepository>(), interceptingTx1, clock);
        var service2 = sp2.GetRequiredService<IInventoryAllocationService>();

        var req1 = new CreateHoldRequest("rental-sys", "REF-NON-1", InitialToday.AddDays(1), InitialToday.AddDays(3), InitialNow.AddHours(2));
        var req2 = new CreateHoldRequest("rental-sys", "REF-NON-2", InitialToday.AddDays(5), InitialToday.AddDays(8), InitialNow.AddHours(2));

        Task<Result<CreateHoldResult>>? task1 = null;
        Task<Result<CreateHoldResult>>? task2 = null;

        try
        {
            task1 = Task.Run(() => service1.CreateHoldAsync(asset.Id, req1, caller));
            await hold1EnteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

            task2 = Task.Run(() => service2.CreateHoldAsync(asset.Id, req2, caller));
            await WaitForAdvisoryLockWaiterAsync(asset.Id, TimeSpan.FromSeconds(15));

            allowHold1ToProceed.TrySetResult(true);

            var res1 = await task1.WaitAsync(TimeSpan.FromSeconds(15));
            var res2 = await task2.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(res1.IsSuccess);
            Assert.True(res2.IsSuccess);

            await using var db = CreateDbContext();
            var count = await db.AssetAllocations.CountAsync(a => a.AssetId == asset.Id);
            Assert.Equal(2, count);

            var outboxCount = await db.OutboxMessages.CountAsync(m => m.KafkaKey == asset.Id.ToString("D"));
            Assert.Equal(2, outboxCount);
        }
        finally
        {
            hold1EnteredLock.TrySetResult(true);
            allowHold1ToProceed.TrySetResult(true);
            if (task1 is not null) await Task.WhenAny(task1, Task.Delay(1000));
            if (task2 is not null) await Task.WhenAny(task2, Task.Delay(1000));
        }
    }

    [Fact]
    public async Task SameBusinessKey_TwoDifferentAssets_UniqueViolationRollbackAndReconciliation_Proven()
    {
        var clock = new TestTimeProvider(InitialNow);

        var tx1LookupDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tx2LookupDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowTx2ToCommit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        int totalLookupCallsOnSp2 = 0;

        using var sp1 = BuildServiceProvider(clock, decorateAllocations: (_, inner) =>
        {
            var intercepting = new InterceptingAllocationRepository(inner);
            intercepting.OnAfterGetByExternalReferenceAsync = async (src, ext) =>
            {
                tx1LookupDone.TrySetResult(true);
                await tx2LookupDone.Task.WaitAsync(TimeSpan.FromSeconds(15));
            };
            return intercepting;
        });

        using var sp2 = BuildServiceProvider(clock, decorateAllocations: (_, inner) =>
        {
            var intercepting = new InterceptingAllocationRepository(inner, onCall: () => Interlocked.Increment(ref totalLookupCallsOnSp2));
            intercepting.OnAfterGetByExternalReferenceAsync = async (src, ext) =>
            {
                if (totalLookupCallsOnSp2 == 1)
                {
                    tx2LookupDone.TrySetResult(true);
                    await tx1LookupDone.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await allowTx2ToCommit.Task.WaitAsync(TimeSpan.FromSeconds(15));
                }
            };
            return intercepting;
        });

        var ownerId = Guid.NewGuid();
        var asset1 = await SeedAssetAsync(ownerId);
        var asset2 = await SeedAssetAsync(ownerId);
        var caller = new ServiceCallerContext();

        string sharedExternalRef = $"SHARED-{Guid.NewGuid():N}";
        var period = new RentalPeriod(InitialToday.AddDays(10), InitialToday.AddDays(15));
        var req1 = new CreateHoldRequest("cross-sys", sharedExternalRef, period.StartDate, period.EndDate, InitialNow.AddHours(2), "Asset 1 note");
        var req2 = new CreateHoldRequest("cross-sys", sharedExternalRef, period.StartDate, period.EndDate, InitialNow.AddHours(2), "Asset 2 note");

        var service1 = sp1.GetRequiredService<IInventoryAllocationService>();
        var service2 = sp2.GetRequiredService<IInventoryAllocationService>();
        Task<Result<CreateHoldResult>>? task1 = null;
        Task<Result<CreateHoldResult>>? task2 = null;

        try
        {
            // 1. Chạy cả 2 tasks
            task1 = Task.Run(() => service1.CreateHoldAsync(asset1.Id, req1, caller));
            task2 = Task.Run(() => service2.CreateHoldAsync(asset2.Id, req2, caller));

            // Chờ cả 2 hoàn tất lookup ban đầu và đều thấy null
            await tx1LookupDone.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await tx2LookupDone.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // Cho task 1 chạy và commit thành công trước
            var res1 = await task1.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(res1.IsSuccess);
            Assert.False(res1.Value.IsReplay);

            // 2. Cho task 2 tiếp tục commit: vấp phải unique violation trong PostgreSQL 17!
            allowTx2ToCommit.TrySetResult(true);

            var res2 = await task2.WaitAsync(TimeSpan.FromSeconds(15));

            // Task 2 thất bại với EXTERNAL_REFERENCE_REUSED sau khi rollback và reconciliation
            Assert.True(res2.IsFailure);
            Assert.Equal("EXTERNAL_REFERENCE_REUSED", res2.Error.Code);

            // Bằng chứng rõ ràng: Task 2 đã thực hiện đúng 2 lần lookup (1 lần ban đầu thấy null + 1 lần reconciliation sau unique violation)!
            Assert.Equal(2, totalLookupCallsOnSp2);

            await using var db = CreateDbContext();
            // Chỉ có đúng 1 allocation và 1 outbox trong DB thuộc về Asset 1
            var allocs = await db.AssetAllocations.Where(a => a.SourceSystem == "cross-sys" && a.ExternalReference == sharedExternalRef).ToListAsync();
            Assert.Single(allocs);
            Assert.Equal(asset1.Id, allocs[0].AssetId);
            Assert.Equal("Asset 1 note", allocs[0].Notes);

            var outbox = await db.OutboxMessages.Where(m => m.KafkaKey == asset1.Id.ToString("D")).ToListAsync();
            Assert.Single(outbox);

            var asset2Allocs = await db.AssetAllocations.Where(a => a.AssetId == asset2.Id).ToListAsync();
            Assert.Empty(asset2Allocs);
        }
        finally
        {
            tx1LookupDone.TrySetResult(true);
            tx2LookupDone.TrySetResult(true);
            allowTx2ToCommit.TrySetResult(true);

            if (task1 is not null) await Task.WhenAny(task1, Task.Delay(1000));
            if (task2 is not null) await Task.WhenAny(task2, Task.Delay(1000));
        }
    }

    [Fact]
    public async Task SameAsset_SamePayload_ConcurrentCreateAndReplay_OnlyOneCreationAndOutbox()
    {
        var clock = new TestTimeProvider(InitialNow);
        using var sp1 = BuildServiceProvider(clock);
        using var sp2 = BuildServiceProvider(clock);

        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);
        var caller = new ServiceCallerContext();

        string sharedExternalRef = $"SAME-{Guid.NewGuid():N}";
        var period = new RentalPeriod(InitialToday.AddDays(5), InitialToday.AddDays(8));
        var req = new CreateHoldRequest("same-sys", sharedExternalRef, period.StartDate, period.EndDate, InitialNow.AddHours(2), "Identical payload");

        var op1EnteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOp1ToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var baseTx1 = sp1.GetRequiredService<IInventoryWriteTransaction>();
        var interceptingTx1 = new InterceptingWriteTransaction(baseTx1, async (ctx, ct) =>
        {
            op1EnteredLock.TrySetResult(true);
            await allowOp1ToProceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        });

        var service1 = new InventoryAllocationService(sp1.GetRequiredService<IAssetAllocationRepository>(), interceptingTx1, clock);
        var service2 = sp2.GetRequiredService<IInventoryAllocationService>();

        Task<Result<CreateHoldResult>>? task1 = null;
        Task<Result<CreateHoldResult>>? task2 = null;

        try
        {
            task1 = Task.Run(() => service1.CreateHoldAsync(asset.Id, req, caller));
            await op1EnteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

            task2 = Task.Run(() => service2.CreateHoldAsync(asset.Id, req, caller));
            await WaitForAdvisoryLockWaiterAsync(asset.Id, TimeSpan.FromSeconds(15));

            allowOp1ToProceed.TrySetResult(true);

            var res1 = await task1.WaitAsync(TimeSpan.FromSeconds(15));
            var res2 = await task2.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(res1.IsSuccess);
            Assert.False(res1.Value.IsReplay);

            Assert.True(res2.IsSuccess);
            Assert.True(res2.Value.IsReplay);

            Assert.Equal(res1.Value.Allocation.Id, res2.Value.Allocation.Id);

            await using var db = CreateDbContext();
            var count = await db.AssetAllocations.CountAsync(a => a.AssetId == asset.Id);
            Assert.Equal(1, count);

            var transitions = await db.AllocationTransitions.Where(t => t.AllocationId == res1.Value.Allocation.Id).ToListAsync();
            Assert.Single(transitions);

            var outboxCount = await db.OutboxMessages.CountAsync(m => m.KafkaKey == asset.Id.ToString("D"));
            Assert.Equal(1, outboxCount);
        }
        finally
        {
            op1EnteredLock.TrySetResult(true);
            allowOp1ToProceed.TrySetResult(true);

            if (task1 is not null) await Task.WhenAny(task1, Task.Delay(1000));
            if (task2 is not null) await Task.WhenAny(task2, Task.Delay(1000));
        }
    }

    [Fact]
    public async Task Concurrent_Allocate_SameKey_OnlyOneTransitionAppended()
    {
        var clock = new TestTimeProvider(InitialNow);
        using var sp1 = BuildServiceProvider(clock);
        using var sp2 = BuildServiceProvider(clock);

        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);
        var caller = new ServiceCallerContext();

        // 1. Tạo Hold ban đầu
        var allocService = sp1.GetRequiredService<IInventoryAllocationService>();
        var holdPeriod = new RentalPeriod(InitialToday.AddDays(10), InitialToday.AddDays(15));
        var holdRes = await allocService.CreateHoldAsync(
            asset.Id,
            new CreateHoldRequest("rental-sys", $"ALLOC-REPLAY-{Guid.NewGuid():N}", holdPeriod.StartDate, holdPeriod.EndDate, InitialNow.AddHours(2)),
            caller);
        Assert.True(holdRes.IsSuccess);
        var allocationId = holdRes.Value.Allocation.Id;

        var alloc1EnteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowAlloc1ToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var baseTx1 = sp1.GetRequiredService<IInventoryWriteTransaction>();
        var interceptingTx1 = new InterceptingWriteTransaction(baseTx1, async (ctx, ct) =>
        {
            alloc1EnteredLock.TrySetResult(true);
            await allowAlloc1ToProceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        });

        var service1 = new InventoryAllocationService(sp1.GetRequiredService<IAssetAllocationRepository>(), interceptingTx1, clock);
        var service2 = sp2.GetRequiredService<IInventoryAllocationService>();

        const string opKey = "OP-IDEMPOTENT-ALLOC";
        Task<Result<AllocationResponse>>? task1 = null;
        Task<Result<AllocationResponse>>? task2 = null;

        try
        {
            task1 = Task.Run(() => service1.AllocateAsync(allocationId, new AllocateRequest(opKey), caller));
            await alloc1EnteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

            task2 = Task.Run(() => service2.AllocateAsync(allocationId, new AllocateRequest(opKey), caller));
            await WaitForAdvisoryLockWaiterAsync(asset.Id, TimeSpan.FromSeconds(15));

            allowAlloc1ToProceed.TrySetResult(true);

            var res1 = await task1.WaitAsync(TimeSpan.FromSeconds(15));
            var res2 = await task2.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(res1.IsSuccess);
            Assert.True(res2.IsSuccess);
            Assert.Equal("Allocated", res1.Value.Status);
            Assert.Equal("Allocated", res2.Value.Status);

            await using var db = CreateDbContext();
            var transitions = await db.AllocationTransitions.Where(t => t.AllocationId == allocationId).OrderBy(t => t.Sequence).ToListAsync();
            Assert.Equal(2, transitions.Count); // 1 CreateHold, 1 Allocate
            Assert.Equal(AllocationOperationType.CreateHold, transitions[0].OperationType);
            Assert.Equal(AllocationOperationType.Allocate, transitions[1].OperationType);
            Assert.Equal(opKey, transitions[1].OperationKey);
        }
        finally
        {
            alloc1EnteredLock.TrySetResult(true);
            allowAlloc1ToProceed.TrySetResult(true);
            if (task1 is not null) await Task.WhenAny(task1, Task.Delay(1000));
            if (task2 is not null) await Task.WhenAny(task2, Task.Delay(1000));
        }
    }

    [Fact]
    public async Task Concurrent_Allocate_DifferentKeys_OnlyOneSucceeds()
    {
        var clock = new TestTimeProvider(InitialNow);
        using var sp1 = BuildServiceProvider(clock);
        using var sp2 = BuildServiceProvider(clock);

        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);
        var caller = new ServiceCallerContext();

        var allocService = sp1.GetRequiredService<IInventoryAllocationService>();
        var holdPeriod = new RentalPeriod(InitialToday.AddDays(10), InitialToday.AddDays(15));
        var holdRes = await allocService.CreateHoldAsync(
            asset.Id,
            new CreateHoldRequest("rental-sys", $"ALLOC-DIFF-{Guid.NewGuid():N}", holdPeriod.StartDate, holdPeriod.EndDate, InitialNow.AddHours(2)),
            caller);
        Assert.True(holdRes.IsSuccess);
        var allocationId = holdRes.Value.Allocation.Id;

        var alloc1EnteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowAlloc1ToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var baseTx1 = sp1.GetRequiredService<IInventoryWriteTransaction>();
        var interceptingTx1 = new InterceptingWriteTransaction(baseTx1, async (ctx, ct) =>
        {
            alloc1EnteredLock.TrySetResult(true);
            await allowAlloc1ToProceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        });

        var service1 = new InventoryAllocationService(sp1.GetRequiredService<IAssetAllocationRepository>(), interceptingTx1, clock);
        var service2 = sp2.GetRequiredService<IInventoryAllocationService>();

        Task<Result<AllocationResponse>>? task1 = null;
        Task<Result<AllocationResponse>>? task2 = null;

        try
        {
            task1 = Task.Run(() => service1.AllocateAsync(allocationId, new AllocateRequest("OP-KEY-A"), caller));
            await alloc1EnteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

            task2 = Task.Run(() => service2.AllocateAsync(allocationId, new AllocateRequest("OP-KEY-B"), caller));
            await WaitForAdvisoryLockWaiterAsync(asset.Id, TimeSpan.FromSeconds(15));

            allowAlloc1ToProceed.TrySetResult(true);

            var res1 = await task1.WaitAsync(TimeSpan.FromSeconds(15));
            var res2 = await task2.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(res1.IsSuccess);
            Assert.True(res2.IsFailure);
            Assert.Equal("INVALID_STATUS_TRANSITION", res2.Error.Code);
            Assert.Equal(ErrorType.Conflict, res2.Error.Type); // Application maps to Conflict directly!

            await using var db = CreateDbContext();
            var transitions = await db.AllocationTransitions.Where(t => t.AllocationId == allocationId).ToListAsync();
            Assert.Equal(2, transitions.Count);
        }
        finally
        {
            alloc1EnteredLock.TrySetResult(true);
            allowAlloc1ToProceed.TrySetResult(true);
            if (task1 is not null) await Task.WhenAny(task1, Task.Delay(1000));
            if (task2 is not null) await Task.WhenAny(task2, Task.Delay(1000));
        }
    }

    [Fact]
    public async Task ClockReadAfterLockWait_DetectsExpiredHold()
    {
        var clock = new TestTimeProvider(InitialNow);
        using var sp1 = BuildServiceProvider(clock);
        using var sp2 = BuildServiceProvider(clock);

        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);
        var caller = new ServiceCallerContext();

        // Tạo 1 Hold hết hạn sau 30 phút
        var allocService = sp1.GetRequiredService<IInventoryAllocationService>();
        var holdPeriod = new RentalPeriod(InitialToday.AddDays(5), InitialToday.AddDays(8));
        var holdRes = await allocService.CreateHoldAsync(
            asset.Id,
            new CreateHoldRequest("rental-sys", $"HOLD-EXP-WAIT-{Guid.NewGuid():N}", holdPeriod.StartDate, holdPeriod.EndDate, InitialNow.AddMinutes(30)),
            caller);
        Assert.True(holdRes.IsSuccess);
        var allocationId = holdRes.Value.Allocation.Id;

        var op1EnteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOp1ToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var tx1 = sp1.GetRequiredService<IInventoryWriteTransaction>();
        var service2 = sp2.GetRequiredService<IInventoryAllocationService>();

        Task<Result<bool>>? task1 = null;
        Task<Result<AllocationResponse>>? task2 = null;

        try
        {
            // 1. Operation 1 giữ lock trên asset
            task1 = Task.Run(async () =>
            {
                return await tx1.ExecuteAsync<bool>(asset.Id, async (ctx, ct) =>
                {
                    op1EnteredLock.TrySetResult(true);
                    await allowOp1ToProceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
                    return Result.Success(true);
                });
            });

            await op1EnteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // 2. Operation 2 chạy Allocate và phải đợi lock
            task2 = Task.Run(() => service2.AllocateAsync(allocationId, new AllocateRequest("OP-LATE-ALLOC"), caller));
            await WaitForAdvisoryLockWaiterAsync(asset.Id, TimeSpan.FromSeconds(15));

            // 3. Trong lúc Operation 2 đang chờ lock, đồng hồ hệ thống chạy tới +2 giờ (vượt quá 30 phút của hold)
            clock.Advance(TimeSpan.FromHours(2));

            // 4. Cho phép Operation 1 commit
            allowOp1ToProceed.TrySetResult(true);

            await task1.WaitAsync(TimeSpan.FromSeconds(15));
            var res2 = await task2.WaitAsync(TimeSpan.FromSeconds(15));

            // Operation 2 thức dậy, đọc clock mới sau khi có lock -> thấy hold đã hết hạn!
            Assert.True(res2.IsFailure);
            Assert.Equal("HOLD_EXPIRED", res2.Error.Code);
            Assert.Equal(ErrorType.Conflict, res2.Error.Type);
        }
        finally
        {
            op1EnteredLock.TrySetResult(true);
            allowOp1ToProceed.TrySetResult(true);
            if (task1 is not null) await Task.WhenAny((Task)task1, Task.Delay(1000));
            if (task2 is not null) await Task.WhenAny((Task)task2, Task.Delay(1000));
        }
    }
}
