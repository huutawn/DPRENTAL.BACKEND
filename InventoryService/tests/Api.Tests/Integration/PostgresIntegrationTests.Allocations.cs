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
    public async Task ExternalReference_PayloadEquivalenceAndConflict()
    {
        using var sp = BuildServiceProvider();
        await using var setupContext = await CreateMigratedDbContextAsync();

        Guid assetId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        setupContext.Assets.Add(
            InventoryAsset.Create(assetId, ownerId, organizationId, Guid.NewGuid(), 1, "{}", true, now).Value);
        await setupContext.SaveChangesAsync();

        const string sourceSystem = "rental_service";
        string externalRef = $"REF_{Guid.NewGuid():N}";
        var period = new RentalPeriod(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 25));

        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();

        // 1. Tạo allocation ban đầu
        Guid originalAllocId = Guid.NewGuid();
        var initialResult = await executor.ExecuteAsync<AssetAllocation>(assetId, async (ctx, ct) =>
        {
            var hold = AssetAllocation.CreateHold(
                originalAllocId, assetId, period, now.AddHours(2), now, sourceSystem, externalRef).Value;
            ctx.Allocations.Add(hold);
            return await Task.FromResult(Result.Success(hold));
        });
        Assert.True(initialResult.IsSuccess);

        async Task<Result<AssetAllocation>> RetryAsync(RentalPeriod requestedPeriod) =>
            await executor.ExecuteAsync<AssetAllocation>(assetId, async (ctx, ct) =>
            {
                var existing = await ctx.Allocations.GetByExternalReferenceAsync(sourceSystem, externalRef, ct);
                if (existing is not null)
                {
                    var retryCheck = existing.ValidateExternalRetry(assetId, requestedPeriod, now.AddHours(2));
                    return retryCheck.IsSuccess ? Result.Success(existing) : retryCheck.Error;
                }
                return Error.NotFound("NOT_FOUND", "Không tìm thấy");
            });

        var equivalent = await RetryAsync(period);
        Assert.True(equivalent.IsSuccess);
        Assert.Equal(originalAllocId, equivalent.Value.Id);

        var different = await RetryAsync(new RentalPeriod(period.StartDate.AddDays(1), period.EndDate));
        Assert.True(different.IsFailure);
        Assert.Equal("EXTERNAL_REFERENCE_REUSED", different.Error.Code);

        await using var verify = await CreateMigratedDbContextAsync();
        Assert.Equal(1, await verify.AssetAllocations.CountAsync(a => a.AssetId == assetId));
    }

    [Fact]
    public async Task TerminalState_RejectsFurtherTransitions()
    {
        using var sp = BuildServiceProvider();
        await using var setupContext = await CreateMigratedDbContextAsync();

        Guid assetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        setupContext.Assets.Add(InventoryAsset.Create(assetId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "{}", true, now).Value);

        var period = new RentalPeriod(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 25));
        var alloc = AssetAllocation.CreateManual(Guid.NewGuid(), assetId, period, now).Value;
        setupContext.AssetAllocations.Add(alloc);
        await setupContext.SaveChangesAsync();

        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();

        // Chuyển sang Cancelled (Terminal)
        var cancelResult = await executor.ExecuteAsync<bool>(assetId, async (ctx, ct) =>
        {
            var a = await ctx.Allocations.GetByIdAsync(alloc.Id, ct);
            Assert.NotNull(a);
            var res = a.Cancel("Khách hủy", now.AddHours(1), "operator");
            if (res.IsSuccess) ctx.Allocations.Update(a);
            return res.IsSuccess ? Result.Success(true) : res.Error;
        });
        Assert.True(cancelResult.IsSuccess);

        // Cố gắng chuyển sang Released sau khi đã Cancelled -> Bị từ chối
        var secondTransition = await executor.ExecuteAsync<bool>(assetId, async (ctx, ct) =>
        {
            var a = await ctx.Allocations.GetByIdAsync(alloc.Id, ct);
            Assert.NotNull(a);
            var res = a.Release("Cố nhả", now.AddHours(2), "operator");
            return res.IsSuccess ? Result.Success(true) : res.Error;
        });

        Assert.True(secondTransition.IsFailure);
        Assert.Equal("ALLOCATION_IN_TERMINAL_STATE", secondTransition.Error.Code);
    }

    [Fact]
    public async Task Availability_ExpiredHeldDoesNotOccupy_AllocatedOccupies_ReleasedAndCancelledDoNotOccupy()
    {
        await using var db = await CreateMigratedDbContextAsync();
        var assetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var asset = InventoryAsset.Create(assetId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "{}", true, now).Value;
        db.Assets.Add(asset);

        // 1. Expired hold
        var period1 = new RentalPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5));
        var expiredHold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, period1, now.AddHours(-1), now.AddHours(-3), "rental", "EXP-01").Value;

        // 2. Active allocated
        var period2 = new RentalPeriod(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 15));
        var allocated = AssetAllocation.CreateManual(Guid.NewGuid(), assetId, period2, now, "ALLOC-01").Value;

        // 3. Released allocation
        var period3 = new RentalPeriod(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 25));
        var released = AssetAllocation.CreateManual(Guid.NewGuid(), assetId, period3, now, "REL-01").Value;
        released.Release("Xong", now.AddHours(1), "op");

        var cancelledPeriod = new RentalPeriod(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 5));
        var cancelled = AssetAllocation.CreateManual(Guid.NewGuid(), assetId, cancelledPeriod, now, "CANCEL-01").Value;
        Assert.True(cancelled.Cancel("Cancelled", now.AddHours(1), "op").IsSuccess);

        db.AssetAllocations.AddRange(expiredHold, allocated, released, cancelled);
        await db.SaveChangesAsync();

        var allAllocations = await db.AssetAllocations.Where(a => a.AssetId == assetId).ToListAsync();

        // Kiểm tra availability cho period1 (trùng với expired hold): Khả dụng!
        var avail1 = AssetAvailabilityPolicy.CheckAvailability(asset, period1, now, allAllocations, []);
        Assert.True(avail1.IsSuccess);

        // Kiểm tra availability cho period2 (trùng với allocated): Bị xung đột!
        var avail2 = AssetAvailabilityPolicy.CheckAvailability(asset, period2, now, allAllocations, []);
        Assert.True(avail2.IsFailure);
        Assert.Equal("ASSET_UNAVAILABLE", avail2.Error.Code);

        // Kiểm tra availability cho period3 (trùng với released): Khả dụng!
        var avail3 = AssetAvailabilityPolicy.CheckAvailability(asset, period3, now, allAllocations, []);
        Assert.True(avail3.IsSuccess);
        Assert.True(AssetAvailabilityPolicy.CheckAvailability(asset, cancelledPeriod, now, allAllocations, []).IsSuccess);
    }

    [Fact]
    public async Task AllocateOrExtend_ExpiredHoldFails()
    {
        var assetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var period = new RentalPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5));
        var hold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, period, now.AddMinutes(-5), now.AddMinutes(-10), "rental", "EXT-EXP").Value;

        // Allocate on expired hold fails
        var allocResult = hold.Allocate(now, "system");
        Assert.True(allocResult.IsFailure);
        Assert.Equal("HOLD_EXPIRED", allocResult.Error.Code);

        // Extend on expired hold fails
        var extendResult = hold.ExtendHold(now.AddHours(1), now, "system");
        Assert.True(extendResult.IsFailure);
        Assert.Equal("HOLD_EXPIRED", extendResult.Error.Code);
    }
}
