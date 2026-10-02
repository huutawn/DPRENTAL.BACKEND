using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Policies;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class PostgresIntegrationTests
{
    [Fact]
    public async Task ExpiredHoldRehydratesHistoryAndPersistsReleaseAndOutboxAtomically()
    {
        using var services = BuildServiceProvider();
        await using var setup = await CreateMigratedDbContextAsync();
        var now = BusinessDateTime.NormalizeUtc(DateTimeOffset.UtcNow);
        var asset = InventoryAsset.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "{}", true, now).Value;
        var start = BusinessDateTime.ToVietnamDate(now).AddDays(1);
        var hold = AssetAllocation.CreateHold(
            Guid.NewGuid(), asset.Id, new RentalPeriod(start, start), now.AddSeconds(-20), now.AddMinutes(-2),
            "tests", Guid.NewGuid().ToString(), null, "system").Value;
        Assert.True(hold.ExtendHold(now.AddSeconds(-10), now.AddMinutes(-1), "system", operationKey: "extend").IsSuccess);

        setup.Assets.Add(asset);
        setup.AssetAllocations.Add(hold);
        await setup.SaveChangesAsync();

        var result = await services.GetRequiredService<IInventoryWriteTransaction>().ExecuteAsync<int>(asset.Id, async (ctx, ct) =>
        {
            var alloc = await ctx.Allocations.GetByIdAsync(hold.Id, ct);
            Assert.NotNull(alloc);
            Assert.True(alloc.ExpireHold(now, "system", "expire").IsSuccess);
            ctx.Allocations.Update(alloc);
            ctx.StageOutbox(OutboxMessage.Create(Guid.NewGuid(), "inventory.allocation.events.v1", $"{hold.Id}", "{}", now));
            return Result.Success(alloc.Transitions.Count);
        });

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(3, result.Value);

        await using var verify = await CreateMigratedDbContextAsync();
        var persisted = await verify.AssetAllocations.Include(r => r.Transitions).SingleAsync(r => r.Id == hold.Id);
        Assert.Equal(AllocationStatus.Released, persisted.Status);
        Assert.Equal(Enumerable.Range(1, 3), persisted.Transitions.OrderBy(t => t.Sequence).Select(t => t.Sequence));
        Assert.Single(await verify.OutboxMessages.Where(m => m.KafkaKey == $"{hold.Id}").ToListAsync());
    }

    [Fact]
    public async Task MultipleAssetsSameEquipmentTypeRoundtrip()
    {
        await using var db = await CreateMigratedDbContextAsync();
        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var equipmentTypeId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var asset1 = InventoryAsset.Create(Guid.NewGuid(), ownerId, orgId, equipmentTypeId, 1, "{\"lens\":\"24-70mm\",\"sn\":\"SN001\"}", true, now).Value;
        var asset2 = InventoryAsset.Create(Guid.NewGuid(), ownerId, orgId, equipmentTypeId, 2, "{\"lens\":\"70-200mm\",\"sn\":\"SN002\"}", true, now).Value;

        db.Assets.AddRange(asset1, asset2);
        await db.SaveChangesAsync();

        await using var verify = await CreateMigratedDbContextAsync();
        var loaded = await verify.Assets.Where(a => a.EquipmentTypeId == equipmentTypeId).OrderBy(a => a.SchemaVersion).ToListAsync();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(ownerId, loaded[0].OwnerUserId);
        Assert.Equal(ownerId, loaded[1].OwnerUserId);
        Assert.Contains("24-70mm", loaded[0].SpecValues);
        Assert.Contains("70-200mm", loaded[1].SpecValues);
        Assert.True(loaded[0].Version > 0);
        Assert.True(loaded[1].Version > 0);
    }

    [Fact]
    public async Task WritesToAnotherAssetRollbackEvenWhenUsingTransactionProtocol()
    {
        using var services = BuildServiceProvider();
        await using var setup = await CreateMigratedDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var asset1 = InventoryAsset.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "{}", true, now).Value;
        var asset2 = InventoryAsset.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "{}", true, now).Value;
        setup.Assets.AddRange(asset1, asset2);
        await setup.SaveChangesAsync();

        var executor = services.GetRequiredService<IInventoryWriteTransaction>();
        var wrongAllocId = Guid.NewGuid();
        var result = await executor.ExecuteAsync<bool>(asset1.Id, (ctx, ct) =>
        {
            var period = new RentalPeriod(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 5));
            var wrongAlloc = AssetAllocation.CreateManual(wrongAllocId, asset2.Id, period, now).Value;
            ctx.Allocations.Add(wrongAlloc);
            return Task.FromResult(Result.Success(true));
        });

        Assert.True(result.IsFailure);
        Assert.Equal("ASSET_LOCK_MISMATCH", result.Error.Code);

        await using var verify = await CreateMigratedDbContextAsync();
        Assert.Null(await verify.AssetAllocations.FindAsync(wrongAllocId));
    }

    [Fact]
    public async Task NestedTransactionFailsWithoutWaitingForItsOwnLock()
    {
        using var services = BuildServiceProvider();
        var executor = services.GetRequiredService<IInventoryWriteTransaction>();
        var assetId = Guid.NewGuid();

        var result = await executor.ExecuteAsync<bool>(assetId, async (outerCtx, ct) =>
        {
            return await executor.ExecuteAsync<bool>(assetId, (innerCtx, innerCt) =>
                Task.FromResult(Result.Success(true)), ct);
        });

        Assert.True(result.IsFailure);
        Assert.Equal("NESTED_INVENTORY_TRANSACTION", result.Error.Code);
    }

    [Fact]
    public async Task CommandFingerprintSurvivesRoundtripAndRejectsChangedPayload()
    {
        using var services = BuildServiceProvider();
        await using var setup = await CreateMigratedDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var asset = InventoryAsset.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "{}", true, now).Value;
        setup.Assets.Add(asset);
        await setup.SaveChangesAsync();

        var executor = services.GetRequiredService<IInventoryWriteTransaction>();
        var allocId = Guid.NewGuid();
        var period = new RentalPeriod(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 5));
        const string opKey = "idemp-001";

        var createResult = await executor.ExecuteAsync<bool>(asset.Id, (ctx, ct) =>
        {
            var hold = AssetAllocation.CreateHold(
                allocId, asset.Id, period, now.AddHours(2), now, "portal", "REF-FINGERPRINT", null, "operator", opKey).Value;
            ctx.Allocations.Add(hold);
            return Task.FromResult(Result.Success(true));
        });
        Assert.True(createResult.IsSuccess);

        // Replay với cùng opKey nhưng thao tác khác (ExtendHold) -> conflict
        var conflictResult = await executor.ExecuteAsync<bool>(asset.Id, async (ctx, ct) =>
        {
            var hold = await ctx.Allocations.GetByIdAsync(allocId, ct);
            Assert.NotNull(hold);
            var extend = hold.ExtendHold(now.AddHours(4), now.AddHours(1), "operator", opKey);
            return extend.IsSuccess ? Result.Success(true) : extend.Error;
        });

        Assert.True(conflictResult.IsFailure);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", conflictResult.Error.Code);
    }
}
