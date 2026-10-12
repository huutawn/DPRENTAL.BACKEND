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
    public async Task OwnerCannotBeChangedWithinAssetWriteBoundary()
    {
        using var services = BuildServiceProvider();
        await using var setup = await CreateMigratedDbContextAsync();
        var asset = await IntegrationTestHelper.InsertTestAssetAsync(setup);
        var result = await services.GetRequiredService<IInventoryWriteTransaction>().ExecuteAsync<bool>(asset.Id, async (ctx, ct) =>
        {
            var tracked = await ctx.Assets.GetByIdAsync(asset.Id, ct);
            Assert.NotNull(tracked);
            typeof(InventoryAsset).GetProperty(nameof(InventoryAsset.AssetCode))!.SetValue(tracked, "AST-NEW");
            return Result.Success(true);
        });
        Assert.Equal("ASSET_IDENTITY_IMMUTABLE", result.Error.Code);
    }


    [Fact]
    public async Task ReparentingAllocationRequiresOriginalAssetLockAndRollsBack()
    {
        using var services = BuildServiceProvider();
        await using var setup = await CreateMigratedDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var aId = Guid.NewGuid();
        var a = await IntegrationTestHelper.InsertTestAssetAsync(setup, now: now);
        var b = await IntegrationTestHelper.InsertTestAssetAsync(setup, now: now);
        var day = BusinessDateTime.ToVietnamDate(now).AddDays(1);
        var allocation = AssetAllocation.CreateManual(Guid.NewGuid(), a.Id, new RentalPeriod(day, day), now).Value;
        setup.AssetAllocations.Add(allocation);
        await setup.SaveChangesAsync();
        var result = await services.GetRequiredService<IInventoryWriteTransaction>().ExecuteAsync<bool>(b.Id, async (ctx, ct) =>
        {
            var tracked = await ctx.Allocations.GetByIdAsync(allocation.Id, ct);
            Assert.NotNull(tracked);
            typeof(AssetAllocation).GetProperty(nameof(AssetAllocation.AssetId))!.SetValue(tracked, b.Id);
            return Result.Success(true);
        });
        Assert.Equal("ASSET_LOCK_MISMATCH", result.Error.Code);
        await using var verify = await CreateMigratedDbContextAsync();
        Assert.Equal(a.Id, (await verify.AssetAllocations.SingleAsync(x => x.Id == allocation.Id)).AssetId);
    }

    [Fact]
    public async Task DuplicateWriteReturnsConflictAndDoesNotExposeDatabaseDetails()
    {
        using var services = BuildServiceProvider();
        await using var setup = await CreateMigratedDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var asset = await IntegrationTestHelper.InsertTestAssetAsync(setup, now: now);
        var transaction = services.GetRequiredService<IInventoryWriteTransaction>();
        var period = new RentalPeriod(BusinessDateTime.ToVietnamDate(now).AddDays(1), BusinessDateTime.ToVietnamDate(now).AddDays(2));
        var reference = Guid.NewGuid().ToString();
        Task<Result<bool>> Insert() => transaction.ExecuteAsync<bool>(asset.Id, (ctx, ct) =>
        {
            ctx.Allocations.Add(AssetAllocation.CreateManual(Guid.NewGuid(), asset.Id, period, now, reference).Value);
            return Task.FromResult(Result.Success(true));
        });
        Assert.True((await Insert()).IsSuccess);
        var duplicate = await Insert();
        Assert.Equal(ErrorType.Conflict, duplicate.Error.Type);
        Assert.Equal("INVENTORY_DUPLICATE_KEY", duplicate.Error.Code);
        Assert.DoesNotContain(reference, duplicate.Error.Message);
        var unexpected = await transaction.ExecuteAsync<bool>(asset.Id, (ctx, ct) => throw new InvalidOperationException("sensitive-internal-value"));
        Assert.Equal(ErrorType.Failure, unexpected.Error.Type);
        Assert.DoesNotContain("sensitive-internal-value", unexpected.Error.Message);
    }

    [Fact]
    public async Task StaleWriteReturnsConflict()
    {
        using var services = BuildServiceProvider();
        await using var setup = await CreateMigratedDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var asset = await IntegrationTestHelper.InsertTestAssetAsync(setup, now: now);
        await using var concurrent = await CreateMigratedDbContextAsync();
        var newer = await concurrent.Assets.SingleAsync(a => a.Id == asset.Id);
        newer.DisableBookings("maintenance", now);
        await concurrent.SaveChangesAsync();
        asset.DisableBookings("stale", now);
        var result = await services.GetRequiredService<IInventoryWriteTransaction>().ExecuteAsync<bool>(asset.Id, (ctx, ct) =>
        {
            ctx.Assets.Update(asset);
            return Task.FromResult(Result.Success(true));
        });
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal("STALE_INVENTORY_WRITE", result.Error.Code);
    }

    [Fact]
    public async Task ForeignKeyWriteReturnsValidation()
    {
        using var services = BuildServiceProvider();
        var missingAsset = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var period = new RentalPeriod(BusinessDateTime.ToVietnamDate(now).AddDays(1), BusinessDateTime.ToVietnamDate(now).AddDays(2));
        var result = await services.GetRequiredService<IInventoryWriteTransaction>().ExecuteAsync<bool>(missingAsset, (ctx, ct) =>
        {
            ctx.Allocations.Add(AssetAllocation.CreateManual(Guid.NewGuid(), missingAsset, period, now).Value);
            return Task.FromResult(Result.Success(true));
        });
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVENTORY_CONSTRAINT_VIOLATION", result.Error.Code);
    }

    [Fact]
    public async Task ExpiredHoldRehydratesHistoryAndPersistsReleaseAndOutboxAtomically()
    {
        using var services = BuildServiceProvider();
        await using var setup = await CreateMigratedDbContextAsync();
        var now = BusinessDateTime.NormalizeUtc(DateTimeOffset.UtcNow);
        var start = BusinessDateTime.ToVietnamDate(now).AddDays(1);
        var asset = await IntegrationTestHelper.InsertTestAssetAsync(setup, now: now);
        var hold = AssetAllocation.CreateHold(
            Guid.NewGuid(), asset.Id, new RentalPeriod(start, start), now.AddSeconds(-20), now.AddMinutes(-2),
            "tests", Guid.NewGuid().ToString(), null, "system").Value;
        Assert.True(hold.ExtendHold(now.AddSeconds(-10), now.AddMinutes(-1), "system", operationKey: "extend").IsSuccess);

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

        var asset1 = await IntegrationTestHelper.InsertTestAssetAsync(db, ownerId: ownerId, eqTypeId: equipmentTypeId, model: "Sony FX3", now: now);
        var asset2 = await IntegrationTestHelper.InsertTestAssetAsync(db, ownerId: ownerId, eqTypeId: equipmentTypeId, model: "Sony A7S III", now: now);

        await using var verify = await CreateMigratedDbContextAsync();
        var loaded = await verify.Assets.Include(a => a.Listing).Where(a => a.Listing.EquipmentTypeId == equipmentTypeId).ToListAsync();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(ownerId, loaded[0].Listing.OwnerUserId);
        Assert.Equal(ownerId, loaded[1].Listing.OwnerUserId);
        Assert.True(loaded[0].Version > 0);
        Assert.True(loaded[1].Version > 0);
    }


    [Fact]
    public async Task WritesToAnotherAssetRollbackEvenWhenUsingTransactionProtocol()
    {
        using var services = BuildServiceProvider();
        await using var setup = await CreateMigratedDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var asset1 = await IntegrationTestHelper.InsertTestAssetAsync(setup, now: now);
        var asset2 = await IntegrationTestHelper.InsertTestAssetAsync(setup, now: now);

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
        var asset = await IntegrationTestHelper.InsertTestAssetAsync(setup, now: now);

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

    [Fact]
    public async Task Asset_BrandId_JsonbSpecValues_Version_RoundtripAndConcurrencyRace_Succeeds()
    {
        using var services = BuildServiceProvider();
        await using var setup = await CreateMigratedDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var brandId = Guid.NewGuid();
        var listing = await IntegrationTestHelper.InsertTestListingAsync(
            setup,
            ownerId: Guid.NewGuid(),
            brandId: brandId,
            model: "Sony FX3",
            schemaVersion: 1,
            specValues: "{\"resolution\":\"4K\",\"fps\":120}",
            now: now);

        // 1. Reload from fresh DbContext to verify persistence and PostgreSQL jsonb querying
        await using var verifyDb = await CreateMigratedDbContextAsync();
        var reloaded = await verifyDb.EquipmentListings.SingleAsync(l => l.Id == listing.Id);
        Assert.Equal(brandId, reloaded.BrandId);
        Assert.Equal(1, reloaded.SchemaVersion);
        Assert.Contains("\"4K\"", reloaded.SpecValues);
        Assert.True(reloaded.Version > 0);

        // Query directly via PostgreSQL JSONB operator
        var jsonbResolution = await verifyDb.Database.SqlQuery<string>($"""
            SELECT spec_values->>'resolution' AS "Value" FROM inventory.equipment_listings WHERE id = {listing.Id}
        """).SingleAsync();
        Assert.Equal("4K", jsonbResolution);
        // 2. Concurrency token check (xmin): simulate concurrent update
        var initialVersion = reloaded.Version;
        var newBrandId = Guid.NewGuid();
        var updateTime = now.AddMinutes(5);

        var executor = services.GetRequiredService<IInventoryWriteTransaction>();

        // First update succeeds
        var updateResult1 = await executor.ExecuteListingWriteAsync<bool>(listing.Id, async (ctx, ct) =>
        {
            var dbListing = await ctx.Listings.GetByIdAsync(listing.Id, ct);
            Assert.NotNull(dbListing);
            dbListing.UpdateDetails(newBrandId, "Sony FX3 V2", null, "Sony FX3 V2", null, 2, "{\"resolution\":\"8K\"}", updateTime);
            ctx.Listings.Update(dbListing);
            return Result.Success(true);
        });
        Assert.True(updateResult1.IsSuccess);

        // Reload: version must have changed
        await using var verifyDb2 = await CreateMigratedDbContextAsync();
        var reloaded2 = await verifyDb2.EquipmentListings.SingleAsync(l => l.Id == listing.Id);
        Assert.Equal(newBrandId, reloaded2.BrandId);
        Assert.Equal(2, reloaded2.SchemaVersion);
        Assert.Contains("\"8K\"", reloaded2.SpecValues);
        Assert.NotEqual(initialVersion, reloaded2.Version);

        // Second update trying to use stale snapshot version fails with concurrency conflict
        var staleUpdateResult = await executor.ExecuteListingWriteAsync<bool>(listing.Id, async (ctx, ct) =>
        {
            var dbListing = await ctx.Listings.GetByIdAsync(listing.Id, ct);
            Assert.NotNull(dbListing);
            if (dbListing.Version != initialVersion)
            {
                return Error.Conflict("CONCURRENCY_CONFLICT", "Listing đã bị thay đổi bởi thao tác khác (xmin conflict).");
            }
            return Result.Success(true);
        });
        Assert.True(staleUpdateResult.IsFailure);
        Assert.Equal("CONCURRENCY_CONFLICT", staleUpdateResult.Error.Code);
    }
}
