using Dprental.InventoryService.Api.Infrastructure.DependencyInjection;
using System.Globalization;
using Dprental.InventoryService.Application.DTOs.Calendar;
using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Policies;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

/// <summary>
/// Kiểm thử đồng thời (Concurrency) và cạnh tranh khóa mức tài sản (pg_advisory_xact_lock)
/// trên cơ sở dữ liệu PostgreSQL thật với quan sát trạng thái khóa qua pg_locks theo DPR-149.
/// </summary>
public sealed class PostgresCalendarConcurrencyTests : IClassFixture<PostgresFixture>
{
    private readonly string _connectionString;
    private static readonly DateTimeOffset InitialNow = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly InitialToday = BusinessDateTime.ToVietnamDate(InitialNow);

    public PostgresCalendarConcurrencyTests(PostgresFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
    }

    private ServiceProvider BuildServiceProvider(TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInventoryServiceDomainRepositories(_connectionString);
        services.AddInventoryApiRepositories();
        services.AddSingleton(timeProvider ?? TimeProvider.System);
        services.AddScoped<CalendarService>();
        services.AddScoped<IInventoryAllocationService, InventoryAllocationService>();
        return services.BuildServiceProvider();
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_connectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    private async Task<InventoryAsset> SeedAssetAsync(Guid ownerUserId, Guid orgId, bool acceptingBookings = true)
    {
        await using var db = CreateDbContext();
        var asset = InventoryAsset.Create(
            Guid.NewGuid(),
            ownerUserId,
            Guid.NewGuid(),
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
                return; // Đã quan sát thấy connection thứ hai đang chờ khóa advisory trong PostgreSQL
            }

            await Task.Delay(20, cts.Token);
        }

        throw new TimeoutException($"Hết thời gian chờ quan sát lock waiter cho asset {assetId} (key: {lockKey}) trong pg_locks.");
    }

    private sealed class TestCallerContext(Guid userId, Guid? orgId = null) : IInventoryCallerContext
    {
        public bool IsAuthenticated => true;
        public bool IsService => false;
        public bool IsAdmin => false;
        public Guid? UserId => userId;
        public IReadOnlySet<string> Permissions => new HashSet<string>();
        public IReadOnlySet<string> Scopes => new HashSet<string>();
        public IReadOnlySet<string> Roles => new HashSet<string>();
        public Guid? OrganizationId => orgId;
    }
    private sealed class TestServiceCallerContext : IInventoryCallerContext
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

    [Fact]
    public async Task Concurrent_Block_Block_Overlap_OnlyOneSucceeds()
    {
        var clock = new TestTimeProvider(InitialNow);
        using var sp1 = BuildServiceProvider(clock);
        using var sp2 = BuildServiceProvider(clock);

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);
        var caller = new TestCallerContext(ownerId, orgId);

        var period = new RentalPeriod(InitialToday.AddDays(10), InitialToday.AddDays(14));

        var block1EnteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBlock1ToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Decorator giữ Operation 1 sau khi đã giành lock
        var baseTx1 = sp1.GetRequiredService<IInventoryWriteTransaction>();
        var interceptingTx1 = new InterceptingWriteTransaction(baseTx1, async (ctx, ct) =>
        {
            block1EnteredLock.SetResult(true);
            await allowBlock1ToProceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        });

        var service1 = new CalendarService(sp1.GetRequiredService<IInventoryAssetRepository>(), interceptingTx1, clock);
        var service2 = sp2.GetRequiredService<CalendarService>();

        var req1 = new CreateCalendarBlockRequest { StartDate = period.StartDate, EndDate = period.EndDate, Reason = CalendarBlockReason.Maintenance };
        var req2 = new CreateCalendarBlockRequest { StartDate = period.StartDate, EndDate = period.EndDate, Reason = CalendarBlockReason.OwnerReserved };

        // 1. Khởi chạy Operation 1
        var task1 = Task.Run(() => service1.CreateCalendarBlockAsync(asset.Id, req1, caller));

        // 2. Chờ Operation 1 giành được khóa advisory
        await block1EnteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // 3. Khởi chạy Operation 2 trên connection độc lập
        var task2 = Task.Run(() => service2.CreateCalendarBlockAsync(asset.Id, req2, caller));

        // 4. Chứng minh qua PostgreSQL catalog pg_locks rằng Operation 2 đang chờ khóa advisory
        await WaitForAdvisoryLockWaiterAsync(asset.Id, TimeSpan.FromSeconds(15));

        // 5. Cho phép Operation 1 hoàn tất và commit
        allowBlock1ToProceed.SetResult(true);

        var res1 = await task1;
        var res2 = await task2;

        Assert.True(res1.IsSuccess);
        Assert.True(res2.IsFailure);
        Assert.Equal("ASSET_BLOCKED", res2.Error.Code);

        // Dữ liệu persisted: đúng 1 block và 1 event trong DB
        await using var db = CreateDbContext();
        var blocks = await db.CalendarBlocks.Where(b => b.AssetId == asset.Id).ToListAsync();
        Assert.Single(blocks);
        Assert.Equal(CalendarBlockReason.Maintenance, blocks[0].Reason);

        var outbox = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync();
        Assert.Single(outbox);
    }

    [Fact]
    public async Task Concurrent_Block_Hold_BlockWinsLockFirst()
    {
        var clock = new TestTimeProvider(InitialNow);
        using var sp1 = BuildServiceProvider(clock);
        using var sp2 = BuildServiceProvider(clock);

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);
        var caller = new TestCallerContext(ownerId, orgId);
        var serviceCaller = new TestServiceCallerContext();
        var period = new RentalPeriod(InitialToday.AddDays(15), InitialToday.AddDays(18));

        var blockEnteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBlockToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var baseTx1 = sp1.GetRequiredService<IInventoryWriteTransaction>();
        var interceptingTx1 = new InterceptingWriteTransaction(baseTx1, async (ctx, ct) =>
        {
            blockEnteredLock.SetResult(true);
            await allowBlockToProceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        });

        var calendarService = new CalendarService(sp1.GetRequiredService<IInventoryAssetRepository>(), interceptingTx1, clock);
        var allocationService = sp2.GetRequiredService<IInventoryAllocationService>();

        var blockReq = new CreateCalendarBlockRequest { StartDate = period.StartDate, EndDate = period.EndDate, Reason = CalendarBlockReason.Maintenance };

        // 1. Block chạy trước, lấy lock
        var blockTask = Task.Run(() => calendarService.CreateCalendarBlockAsync(asset.Id, blockReq, caller));
        await blockEnteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // 2. Hold chạy sau qua application flow thực tế
        var holdReq = new CreateHoldRequest("rental", "EXT-BLOCK-FIRST", period.StartDate, period.EndDate, clock.GetUtcNow().AddHours(2));
        var holdTask = Task.Run(() => allocationService.CreateHoldAsync(asset.Id, holdReq, serviceCaller));

        // 3. Quan sát pg_locks chứng minh Hold bị chặn bởi lock của Block
        await WaitForAdvisoryLockWaiterAsync(asset.Id, TimeSpan.FromSeconds(15));

        // 4. Cho phép Block commit
        allowBlockToProceed.SetResult(true);

        var blockRes = await blockTask;
        var holdRes = await holdTask;

        Assert.True(blockRes.IsSuccess);
        Assert.True(holdRes.IsFailure);
        Assert.Equal("ASSET_BLOCKED", holdRes.Error.Code);

        await using var db = CreateDbContext();
        Assert.Single(await db.CalendarBlocks.Where(b => b.AssetId == asset.Id).ToListAsync());
        Assert.Empty(await db.AssetAllocations.Where(a => a.AssetId == asset.Id).ToListAsync());
        Assert.Single(await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync());
    }

    [Fact]
    public async Task Concurrent_Block_Hold_HoldWinsLockFirst()
    {
        var clock = new TestTimeProvider(InitialNow);
        using var sp1 = BuildServiceProvider(clock);
        using var sp2 = BuildServiceProvider(clock);

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);
        var caller = new TestCallerContext(ownerId, orgId);
        var serviceCaller = new TestServiceCallerContext();
        var period = new RentalPeriod(InitialToday.AddDays(20), InitialToday.AddDays(24));

        var holdEnteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowHoldToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var baseTx1 = sp1.GetRequiredService<IInventoryWriteTransaction>();
        var interceptingTx1 = new InterceptingWriteTransaction(baseTx1, async (ctx, ct) =>
        {
            holdEnteredLock.SetResult(true);
            await allowHoldToProceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        });
        var allocationService1 = new InventoryAllocationService(sp1.GetRequiredService<IAssetAllocationRepository>(), interceptingTx1, clock);
        var calendarService = sp2.GetRequiredService<CalendarService>();

        // 1. Hold chạy trước và lấy lock qua application flow thực tế
        var holdReq = new CreateHoldRequest("rental", "EXT-HOLD-FIRST", period.StartDate, period.EndDate, clock.GetUtcNow().AddHours(2));
        var holdTask = Task.Run(() => allocationService1.CreateHoldAsync(asset.Id, holdReq, serviceCaller));

        await holdEnteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // 2. Block chạy qua application use case thực tế
        var blockReq = new CreateCalendarBlockRequest { StartDate = period.StartDate, EndDate = period.EndDate, Reason = CalendarBlockReason.Maintenance };
        var blockTask = Task.Run(() => calendarService.CreateCalendarBlockAsync(asset.Id, blockReq, caller));

        // 3. Quan sát pg_locks chứng minh Block đang bị chặn bởi lock của Hold
        await WaitForAdvisoryLockWaiterAsync(asset.Id, TimeSpan.FromSeconds(15));

        // 4. Cho phép Hold commit
        allowHoldToProceed.SetResult(true);

        var holdRes = await holdTask;
        var blockRes = await blockTask;

        Assert.True(holdRes.IsSuccess);
        Assert.True(blockRes.IsFailure);
        Assert.Equal("ASSET_UNAVAILABLE", blockRes.Error.Code);

        await using var db = CreateDbContext();
        Assert.Single(await db.AssetAllocations.Where(a => a.AssetId == asset.Id).ToListAsync());
        Assert.Empty(await db.CalendarBlocks.Where(b => b.AssetId == asset.Id).ToListAsync());
    }

    [Fact]
    public async Task Concurrent_NonOverlapping_Blocks_BothSucceed()
    {
        var clock = new TestTimeProvider(InitialNow);
        using var sp1 = BuildServiceProvider(clock);
        using var sp2 = BuildServiceProvider(clock);

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);
        var caller = new TestCallerContext(ownerId, orgId);

        var periodA = new RentalPeriod(InitialToday.AddDays(5), InitialToday.AddDays(7));
        var periodB = new RentalPeriod(InitialToday.AddDays(10), InitialToday.AddDays(12));

        var block1EnteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBlock1ToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var baseTx1 = sp1.GetRequiredService<IInventoryWriteTransaction>();
        var interceptingTx1 = new InterceptingWriteTransaction(baseTx1, async (ctx, ct) =>
        {
            block1EnteredLock.SetResult(true);
            await allowBlock1ToProceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        });

        var service1 = new CalendarService(sp1.GetRequiredService<IInventoryAssetRepository>(), interceptingTx1, clock);
        var service2 = sp2.GetRequiredService<CalendarService>();

        var reqA = new CreateCalendarBlockRequest { StartDate = periodA.StartDate, EndDate = periodA.EndDate, Reason = CalendarBlockReason.Maintenance };
        var reqB = new CreateCalendarBlockRequest { StartDate = periodB.StartDate, EndDate = periodB.EndDate, Reason = CalendarBlockReason.OwnerReserved };

        var taskA = Task.Run(() => service1.CreateCalendarBlockAsync(asset.Id, reqA, caller));
        await block1EnteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var taskB = Task.Run(() => service2.CreateCalendarBlockAsync(asset.Id, reqB, caller));
        await WaitForAdvisoryLockWaiterAsync(asset.Id, TimeSpan.FromSeconds(15));

        allowBlock1ToProceed.SetResult(true);

        var resA = await taskA;
        var resB = await taskB;

        Assert.True(resA.IsSuccess);
        Assert.True(resB.IsSuccess);

        await using var db = CreateDbContext();
        Assert.Equal(2, await db.CalendarBlocks.CountAsync(b => b.AssetId == asset.Id));
        Assert.Equal(2, await db.OutboxMessages.CountAsync(m => m.KafkaKey == asset.Id.ToString("D")));
    }

    [Fact]
    public async Task Cancellation_PreCancelledToken_ThrowsAndReleasesLockImmediately()
    {
        using var sp = BuildServiceProvider();
        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();

        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, Guid.NewGuid());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Token bị cancel trước khi chạy
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync<bool>(asset.Id, (ctx, ct) => Task.FromResult(Result.Success(true)), cts.Token));

        // Lần gọi tiếp theo thành công ngay trong timeout, chứng minh lock không bị giữ
        var secondResult = await executor.ExecuteAsync<bool>(asset.Id, (ctx, ct) => Task.FromResult(Result.Success(true)));
        Assert.True(secondResult.IsSuccess);
    }

    [Fact]
    public async Task Cancellation_AfterAcquiringLockAndStaging_RollsBackAllMutations()
    {
        using var sp = BuildServiceProvider();
        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);

        using var opCts = new CancellationTokenSource();

        var enteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = Task.Run(async () =>
        {
            return await executor.ExecuteAsync<bool>(asset.Id, async (ctx, ct) =>
            {
                var trackedAsset = await ctx.Assets.GetByIdAsync(asset.Id, ct);
                Assert.NotNull(trackedAsset);

                // Stage block
                var block = CalendarBlock.Create(
                    Guid.NewGuid(), asset.Id, InitialToday.AddDays(5), InitialToday.AddDays(8),
                    CalendarBlockReason.Maintenance, ownerId.ToString("D"), InitialNow, InitialToday).Value;
                ctx.CalendarBlocks.Add(block);

                // Stage outbox
                var outboxMsg = InventoryAssetAvailabilityEvents.CreateAvailabilityChangedOutboxMessage(asset.Id, InitialNow);
                ctx.StageOutbox(outboxMsg);

                enteredLock.SetResult(true);

                // Chờ bị cancel
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return Result.Success(true);
            }, opCts.Token);
        });

        await enteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Cancel operation sau khi đã giữ lock và stage dữ liệu
        opCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        // Khẳng định 1: Dữ liệu bị rollback hoàn toàn trong PostgreSQL
        await using (var db = CreateDbContext())
        {
            Assert.Empty(await db.CalendarBlocks.Where(b => b.AssetId == asset.Id).ToListAsync());
            Assert.Empty(await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync());
        }

        // Khẳng định 2: Transaction tiếp theo lấy được cùng lock ngay trong timeout
        var nextResult = await executor.ExecuteAsync<bool>(asset.Id, (ctx, ct) => Task.FromResult(Result.Success(true)));
        Assert.True(nextResult.IsSuccess);
    }

    [Fact]
    public async Task Cancellation_WhileWaitingForLock_AbortsWithoutMutations()
    {
        using var sp1 = BuildServiceProvider();
        using var sp2 = BuildServiceProvider();

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);

        var tx1 = sp1.GetRequiredService<IInventoryWriteTransaction>();
        var tx2 = sp2.GetRequiredService<IInventoryWriteTransaction>();

        var op1EnteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOp1ToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task1 = Task.Run(async () =>
        {
            return await tx1.ExecuteAsync<bool>(asset.Id, async (ctx, ct) =>
            {
                var tracked = await ctx.Assets.GetByIdAsync(asset.Id, ct);
                Assert.NotNull(tracked);

                op1EnteredLock.SetResult(true);
                await allowOp1ToProceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

                var block = CalendarBlock.Create(
                    Guid.NewGuid(), asset.Id, InitialToday.AddDays(5), InitialToday.AddDays(7),
                    CalendarBlockReason.Maintenance, ownerId.ToString("D"), InitialNow, InitialToday).Value;
                ctx.CalendarBlocks.Add(block);
                return Result.Success(true);
            });
        });

        await op1EnteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Khởi chạy Operation 2 với cancellation token riêng
        using var op2Cts = new CancellationTokenSource();
        var task2 = Task.Run(async () =>
        {
            return await tx2.ExecuteAsync<bool>(asset.Id, (ctx, ct) => Task.FromResult(Result.Success(true)), op2Cts.Token);
        });

        // Quan sát Operation 2 đang chờ lock trong PostgreSQL
        await WaitForAdvisoryLockWaiterAsync(asset.Id, TimeSpan.FromSeconds(15));

        // Hủy Operation 2 trong lúc đang chờ lock
        op2Cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task2);

        // Cho phép Operation 1 hoàn thành
        allowOp1ToProceed.SetResult(true);
        var res1 = await task1;
        Assert.True(res1.IsSuccess);

        await using var db = CreateDbContext();
        Assert.Single(await db.CalendarBlocks.Where(b => b.AssetId == asset.Id).ToListAsync());
    }

    [Fact]
    public async Task ClockAdvance_DuringLockWait_ExpiredHoldDoesNotBlock()
    {
        // Clock bắt đầu tại InitialNow
        var clock = new TestTimeProvider(InitialNow);
        using var sp1 = BuildServiceProvider(clock);
        using var sp2 = BuildServiceProvider(clock);

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);
        var caller = new TestCallerContext(ownerId, orgId);

        var period = new RentalPeriod(InitialToday.AddDays(5), InitialToday.AddDays(8));

        // Seed 1 Hold có hạn hết vào InitialNow + 1 giờ
        await using (var db = CreateDbContext())
        {
            var hold = AssetAllocation.CreateHold(
                Guid.NewGuid(), asset.Id, period,
                holdExpiresAt: InitialNow.AddHours(1),
                now: InitialNow,
                sourceSystem: "rental",
                externalReference: "EXT-HOLD-EXPIRING").Value;
            db.AssetAllocations.Add(hold);
            await db.SaveChangesAsync();
        }

        var op1EnteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOp1ToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var tx1 = sp1.GetRequiredService<IInventoryWriteTransaction>();
        var service2 = sp2.GetRequiredService<CalendarService>();

        // 1. Operation 1 giữ lock
        var task1 = Task.Run(async () =>
        {
            return await tx1.ExecuteAsync<bool>(asset.Id, async (ctx, ct) =>
            {
                op1EnteredLock.SetResult(true);
                await allowOp1ToProceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
                return Result.Success(true);
            });
        });

        await op1EnteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // 2. Operation 2 (CreateCalendarBlockAsync) chạy và chờ lock
        var req = new CreateCalendarBlockRequest { StartDate = period.StartDate, EndDate = period.EndDate, Reason = CalendarBlockReason.Maintenance };
        var task2 = Task.Run(() => service2.CreateCalendarBlockAsync(asset.Id, req, caller));

        // Quan sát Operation 2 đang chờ lock
        await WaitForAdvisoryLockWaiterAsync(asset.Id, TimeSpan.FromSeconds(15));

        // 3. Trong lúc Operation 2 đang chờ lock: dịch chuyển clock tiến 2 giờ (làm Hold bị hết hạn tại thời điểm Op 2 lấy lock!)
        clock.Advance(TimeSpan.FromHours(2));

        // 4. Cho phép Operation 1 nhả lock
        allowOp1ToProceed.SetResult(true);

        await task1;
        var res2 = await task2;

        // Chứng minh: Operation 2 lấy 'now' SAU KHI ĐÃ GIÀNH LOCK, thấy Hold đã hết hạn -> TẠO BLOCK THÀNH CÔNG!
        Assert.True(res2.IsSuccess);

        await using var dbFinal = CreateDbContext();
        Assert.Single(await dbFinal.CalendarBlocks.Where(b => b.AssetId == asset.Id).ToListAsync());
        Assert.Single(await dbFinal.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync());
    }

    [Fact]
    public async Task ClockAdvance_DuringLockWait_StartDateBecomesPast_Rejected()
    {
        // Clock bắt đầu tại InitialNow
        var clock = new TestTimeProvider(InitialNow);
        using var sp1 = BuildServiceProvider(clock);
        using var sp2 = BuildServiceProvider(clock);

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);
        var caller = new TestCallerContext(ownerId, orgId);

        // Yêu cầu block bắt đầu đúng ngày hôm nay (InitialToday: 2026-10-02)
        var req = new CreateCalendarBlockRequest { StartDate = InitialToday, EndDate = InitialToday.AddDays(2), Reason = CalendarBlockReason.Maintenance };

        var op1EnteredLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOp1ToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var tx1 = sp1.GetRequiredService<IInventoryWriteTransaction>();
        var service2 = sp2.GetRequiredService<CalendarService>();

        // 1. Operation 1 giữ lock
        var task1 = Task.Run(async () =>
        {
            return await tx1.ExecuteAsync<bool>(asset.Id, async (ctx, ct) =>
            {
                op1EnteredLock.SetResult(true);
                await allowOp1ToProceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
                return Result.Success(true);
            });
        });

        await op1EnteredLock.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // 2. Operation 2 chạy và chờ lock
        var task2 = Task.Run(() => service2.CreateCalendarBlockAsync(asset.Id, req, caller));
        await WaitForAdvisoryLockWaiterAsync(asset.Id, TimeSpan.FromSeconds(15));

        // 3. Trong lúc Operation 2 đang chờ lock: dịch chuyển clock sang ngày hôm sau (2026-10-03)
        clock.Advance(TimeSpan.FromDays(1));

        // 4. Cho phép Operation 1 nhả lock
        allowOp1ToProceed.SetResult(true);

        await task1;
        var res2 = await task2;

        // Chứng minh: Operation 2 tính 'today' SAU KHI ĐÃ GIÀNH LOCK -> StartDate (2026-10-02) đã rơi vào quá khứ -> Bị reject!
        Assert.True(res2.IsFailure);
        Assert.Equal("START_DATE_IN_PAST", res2.Error.Code);

        await using var dbFinal = CreateDbContext();
        Assert.Empty(await dbFinal.CalendarBlocks.Where(b => b.AssetId == asset.Id).ToListAsync());
    }
}
