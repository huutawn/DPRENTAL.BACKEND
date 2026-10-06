using System.Data;
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
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class PostgresIntegrationTests
{
    [Fact]
    public async Task Concurrency_TwoOverlappingRequests_OnlyOneSucceeds()
    {
        using var sp = BuildServiceProvider();
        await using var setupContext = await CreateMigratedDbContextAsync();

        Guid assetId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        Guid orgId = Guid.NewGuid();
        Guid typeId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var rate1 = AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", now).Value;
        setupContext.Assets.Add(InventoryAsset.Create(assetId, ownerId, typeId, Guid.NewGuid(), "Sony FX3", 1, "{}", now, rate1).Value);
        await setupContext.SaveChangesAsync();

        var sharedPeriod = new RentalPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5));
        var readySignal = new TaskCompletionSource<bool>();
        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();

        async Task<Result<AssetAllocation>> SubmitHoldAsync(string externalRef)
        {
            await readySignal.Task;
            return await executor.ExecuteAsync<AssetAllocation>(assetId, async (ctx, ct) =>
            {
                var asset = await ctx.Assets.GetByIdAsync(assetId, ct);
                Assert.NotNull(asset);

                var candidates = await ctx.Allocations.GetActiveCandidatesAsync(assetId, sharedPeriod.StartDate, sharedPeriod.EndDate, ct);
                var blocks = await ctx.CalendarBlocks.GetOverlappingBlocksAsync(assetId, sharedPeriod.StartDate, sharedPeriod.EndDate, ct);

                var availResult = AssetAvailabilityPolicy.CheckAvailability(asset, sharedPeriod, DateTimeOffset.UtcNow, candidates, blocks);
                if (availResult.IsFailure)
                {
                    return availResult.Error;
                }

                var hold = AssetAllocation.CreateHold(
                    Guid.NewGuid(), assetId, sharedPeriod, DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow,
                    "rental_service", externalRef).Value;

                ctx.Allocations.Add(hold);
                return Result.Success(hold);
            });
        }

        var taskA = Task.Run(() => SubmitHoldAsync("EXT-CONCUR-A"));
        var taskB = Task.Run(() => SubmitHoldAsync("EXT-CONCUR-B"));

        readySignal.SetResult(true);
        var results = await Task.WhenAll(taskA, taskB);

        var successes = results.Count(r => r.IsSuccess);
        var conflicts = results.Count(r => r.IsFailure && r.Error.Code == "ASSET_UNAVAILABLE");

        Assert.Equal(1, successes);
        Assert.Equal(1, conflicts);

        await using var verifyContext = await CreateMigratedDbContextAsync();
        var savedAllocations = await verifyContext.AssetAllocations.Where(a => a.AssetId == assetId).ToListAsync();
        Assert.Single(savedAllocations);
    }

    [Fact]
    public async Task Concurrency_TwoNonOverlappingRequests_BothSucceed()
    {
        using var sp = BuildServiceProvider();
        await using var setupContext = await CreateMigratedDbContextAsync();

        Guid assetId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        Guid orgId = Guid.NewGuid();
        Guid typeId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var rate2 = AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", now).Value;
        setupContext.Assets.Add(InventoryAsset.Create(assetId, ownerId, typeId, Guid.NewGuid(), "Sony FX3", 1, "{}", now, rate2).Value);
        await setupContext.SaveChangesAsync();

        var periodA = new RentalPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5));
        var periodB = new RentalPeriod(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 15));

        var readySignal = new TaskCompletionSource<bool>();
        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();

        async Task<Result<AssetAllocation>> SubmitAsync(RentalPeriod p, string extRef)
        {
            await readySignal.Task;
            return await executor.ExecuteAsync<AssetAllocation>(assetId, async (ctx, ct) =>
            {
                var asset = await ctx.Assets.GetByIdAsync(assetId, ct);
                Assert.NotNull(asset);

                var candidates = await ctx.Allocations.GetActiveCandidatesAsync(assetId, p.StartDate, p.EndDate, ct);
                var blocks = await ctx.CalendarBlocks.GetOverlappingBlocksAsync(assetId, p.StartDate, p.EndDate, ct);

                var availResult = AssetAvailabilityPolicy.CheckAvailability(asset, p, DateTimeOffset.UtcNow, candidates, blocks);
                if (availResult.IsFailure)
                {
                    return availResult.Error;
                }

                var hold = AssetAllocation.CreateHold(
                    Guid.NewGuid(), assetId, p, DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow,
                    "rental_service", extRef).Value;

                ctx.Allocations.Add(hold);
                return Result.Success(hold);
            });
        }

        var taskA = Task.Run(() => SubmitAsync(periodA, "EXT-NON-A"));
        var taskB = Task.Run(() => SubmitAsync(periodB, "EXT-NON-B"));

        readySignal.SetResult(true);
        var results = await Task.WhenAll(taskA, taskB);

        Assert.All(results, r => Assert.True(r.IsSuccess));

        await using var verifyContext = await CreateMigratedDbContextAsync();
        Assert.Equal(2, await verifyContext.AssetAllocations.CountAsync(a => a.AssetId == assetId));
    }

    [Fact]
    public async Task WriteTransaction_Rollback_LeavesNoDataOrOutbox()
    {
        using var sp = BuildServiceProvider();
        await using var setupContext = await CreateMigratedDbContextAsync();

        Guid assetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var rate3 = AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", now).Value;
        setupContext.Assets.Add(InventoryAsset.Create(assetId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Sony FX3", 1, "{}", now, rate3).Value);
        await setupContext.SaveChangesAsync();

        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();
        Guid allocId = Guid.NewGuid();
        Guid outboxId = Guid.NewGuid();
        var result = await executor.ExecuteAsync<bool>(assetId, (ctx, ct) =>
        {
            var p = new RentalPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5));
            var alloc = AssetAllocation.CreateManual(allocId, assetId, p, now).Value;
            ctx.Allocations.Add(alloc);

            ctx.StageOutbox(OutboxMessage.Create(outboxId, "test.topic", "key", "{}", now));

            return Task.FromResult<Result<bool>>(Error.Validation("REJECTED", "Lỗi nghiệp vụ mô phỏng"));
        });

        Assert.True(result.IsFailure);

        await using var verify = await CreateMigratedDbContextAsync();
        Assert.Null(await verify.AssetAllocations.FindAsync(allocId));
        Assert.Null(await verify.OutboxMessages.FindAsync(outboxId));
    }

    [Fact]
    public async Task AdvisoryLock_ReleasedWhenOperationCancelled()
    {
        using var sp = BuildServiceProvider();
        await using var setup = await CreateMigratedDbContextAsync();

        Guid assetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var rate4 = AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", now).Value;
        setup.Assets.Add(InventoryAsset.Create(assetId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Sony FX3", 1, "{}", now, rate4).Value);
        await setup.SaveChangesAsync();

        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Lần gọi 1 bị hủy
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync<bool>(assetId, (ctx, ct) =>
                Task.FromResult(Result.Success(true)), cts.Token));

        // Lần gọi 2 chạy bình thường, chứng minh lock đã được giải phóng
        var successResult = await executor.ExecuteAsync<bool>(assetId, (ctx, ct) =>
            Task.FromResult(Result.Success(true)));

        Assert.True(successResult.IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllocationWaitsForConcurrentBlockOrDisable(bool disableBookings)
    {
        using var sp = BuildServiceProvider();
        await using var setup = await CreateMigratedDbContextAsync();

        Guid assetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var rate5 = AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", now).Value;
        setup.Assets.Add(InventoryAsset.Create(assetId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Sony FX3", 1, "{}", now, rate5).Value);
        await setup.SaveChangesAsync();

        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();
        var period = new RentalPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5));

        var blockStarted = new TaskCompletionSource<bool>();
        var proceedBlock = new TaskCompletionSource<bool>();

        var blockTask = Task.Run(async () =>
        {
            return await executor.ExecuteAsync<bool>(assetId, async (ctx, ct) =>
            {
                var asset = await ctx.Assets.GetByIdAsync(assetId, ct);
                Assert.NotNull(asset);

                blockStarted.SetResult(true);
                await proceedBlock.Task;

                if (disableBookings)
                {
                    asset.DisableBookings("Tạm dừng", DateTimeOffset.UtcNow);
                    ctx.Assets.Update(asset);
                }
                else
                {
                    var block = CalendarBlock.Create(
                        Guid.NewGuid(), assetId, period.StartDate, period.EndDate,
                        CalendarBlockReason.Maintenance, "op", DateTimeOffset.UtcNow, new DateOnly(2026, 10, 1)).Value;
                    ctx.CalendarBlocks.Add(block);
                }

                return Result.Success(true);
            });
        });

        await blockStarted.Task;

        var allocTask = Task.Run(async () =>
        {
            return await executor.ExecuteAsync<AssetAllocation>(assetId, async (ctx, ct) =>
            {
                var asset = await ctx.Assets.GetByIdAsync(assetId, ct);
                Assert.NotNull(asset);

                var candidates = await ctx.Allocations.GetActiveCandidatesAsync(assetId, period.StartDate, period.EndDate, ct);
                var blocks = await ctx.CalendarBlocks.GetOverlappingBlocksAsync(assetId, period.StartDate, period.EndDate, ct);

                var availResult = AssetAvailabilityPolicy.CheckAvailability(asset, period, DateTimeOffset.UtcNow, candidates, blocks);
                if (availResult.IsFailure)
                {
                    return availResult.Error;
                }

                var hold = AssetAllocation.CreateHold(
                    Guid.NewGuid(), assetId, period, DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow,
                    "rental", "EXT-LOCKED").Value;
                ctx.Allocations.Add(hold);
                return Result.Success(hold);
            });
        });

        proceedBlock.SetResult(true);
        var blockResult = await blockTask;
        var allocResult = await allocTask;

        Assert.True(blockResult.IsSuccess);
        Assert.True(allocResult.IsFailure);
        Assert.True(allocResult.Error.Code is "ASSET_BLOCKED" or "ASSET_NOT_ACCEPTING_BOOKINGS");
    }

    [Fact]
    public async Task Concurrency_ExpireCompetesWithAllocate()
    {
        using var sp = BuildServiceProvider();
        await using var setup = await CreateMigratedDbContextAsync();

        Guid assetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var rate6 = AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", now).Value;
        setup.Assets.Add(InventoryAsset.Create(assetId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Sony FX3", 1, "{}", now, rate6).Value);

        var period = new RentalPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5));
        var hold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, period, now.AddSeconds(1), now, "rental", "EXT-EXP-COMPETE").Value;
        setup.AssetAllocations.Add(hold);
        await setup.SaveChangesAsync();

        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();

        // Giả lập thời gian sau khi hold hết hạn
        DateTimeOffset afterExpiry = now.AddSeconds(5);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expireTask = Task.Run(async () =>
        {
            await start.Task;
            return await executor.ExecuteAsync<bool>(assetId, async (ctx, ct) =>
        {
            var alloc = await ctx.Allocations.GetByIdAsync(hold.Id, ct);
            Assert.NotNull(alloc);
            var res = alloc.ExpireHold(afterExpiry, "system");
            if (res.IsSuccess) ctx.Allocations.Update(alloc);
            return res.IsSuccess ? Result.Success(true) : res.Error;
        });

        });

        var allocateTask = Task.Run(async () =>
        {
            await start.Task;
            return await executor.ExecuteAsync<bool>(assetId, async (ctx, ct) =>
        {
            var alloc = await ctx.Allocations.GetByIdAsync(hold.Id, ct);
            Assert.NotNull(alloc);
            var res = alloc.Allocate(afterExpiry, "system");
            if (res.IsSuccess) ctx.Allocations.Update(alloc);
            return res.IsSuccess ? Result.Success(true) : res.Error;
        });

        });

        start.SetResult();
        await Task.WhenAll(expireTask, allocateTask);
        var expireResult = await expireTask;
        var allocateResult = await allocateTask;
        Assert.True(expireResult.IsSuccess);
        Assert.True(allocateResult.IsFailure);
        Assert.True(allocateResult.Error.Code is "ALLOCATION_IN_TERMINAL_STATE" or "HOLD_EXPIRED", allocateResult.Error.Code);

        await using var verify = await CreateMigratedDbContextAsync();
        var saved = await verify.AssetAllocations.Include(a => a.Transitions).SingleAsync(a => a.Id == hold.Id);
        Assert.Equal(AllocationStatus.Released, saved.Status);
        Assert.Single(saved.Transitions, t => t.ToStatus == AllocationStatus.Released);
    }
}
