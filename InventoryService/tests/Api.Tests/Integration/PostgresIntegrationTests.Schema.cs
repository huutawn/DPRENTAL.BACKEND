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
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class PostgresIntegrationTests
{
    [Fact]
    public async Task MigrationHistory_IsOwnedByInventoryAndDoesNotChangePublicHistory()
    {
        await using var context = await CreateMigratedDbContextAsync();
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE public."__EFMigrationsHistory" (
                "MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
            INSERT INTO public."__EFMigrationsHistory" VALUES ('auth-sentinel', '10.0.12');
            """);

        await context.Database.MigrateAsync();
        await context.Database.MigrateAsync();

        var inventoryHistoryExists = await context.Database.SqlQueryRaw<bool>(
            """
            SELECT to_regclass('inventory."__EFMigrationsHistory"') IS NOT NULL AS "Value"
            """
        ).SingleAsync();
        Assert.True(inventoryHistoryExists);
        Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync());
        var publicHistory = await context.Database.SqlQueryRaw<string>(
            """
            SELECT "MigrationId" AS "Value" FROM public."__EFMigrationsHistory"
            """
        ).ToListAsync();
        Assert.Equal("auth-sentinel", Assert.Single(publicHistory));
    }

    [Fact]
    public async Task Migration_AppliesSuccessfullyOnPostgres()
    {
        await using var context = await CreateMigratedDbContextAsync();

        // 1. Kiểm tra 6 bảng chính của target domain tồn tại
        var expectedTables = new[]
        {
            "assets",
            "asset_allocations",
            "allocation_transitions",
            "calendar_blocks",
            "outbox_messages",
            "inbox_messages"
        };

        var actualTables = await context.Database.SqlQueryRaw<string>("""
            SELECT table_name AS "Value"
            FROM information_schema.tables
            WHERE table_schema = 'inventory' AND table_type = 'BASE TABLE';
            """).ToListAsync();

        foreach (var expectedTable in expectedTables)
        {
            Assert.Contains(expectedTable, actualTables);
        }

        // 2. Kiểm tra các bảng obsolete KHÔNG tồn tại
        var obsoleteTables = new[] { "notifications", "damage_reports", "saga_instances", "equipments", "reservations", "reservation_transitions" };
        foreach (var obsolete in obsoleteTables)
        {
            Assert.DoesNotContain(obsolete, actualTables);
        }

        // 3. Kiểm tra sequence ReservationNumberSequence đã bị xóa
        var sequences = await context.Database.SqlQueryRaw<string>("""
            SELECT sequence_name AS "Value"
            FROM information_schema.sequences
            WHERE sequence_schema = 'inventory';
            """).ToListAsync();

        Assert.DoesNotContain("ReservationNumberSequence", sequences);
    }

    [Fact]
    public async Task ForeignKey_RestrictDeleteBehavior_PreventsCascadingDeletes()
    {
        await using var context = await CreateMigratedDbContextAsync();

        var assetId1 = Guid.NewGuid();
        var asset = InventoryAsset.Create(
            assetId1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "Sony FX3", 1, "{}", DateTimeOffset.UtcNow, AssetRentalRate.Create(assetId1, 100_000m, null, null, "VND", DateTimeOffset.UtcNow).Value).Value;
        context.Assets.Add(asset);

        var period = new RentalPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5));
        var alloc = AssetAllocation.CreateManual(Guid.NewGuid(), asset.Id, period, DateTimeOffset.UtcNow).Value;
        context.AssetAllocations.Add(alloc);

        await context.SaveChangesAsync();

        // Cố gắng xóa Asset trực tiếp qua SQL để kiểm chứng PostgreSQL FK constraint
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM inventory.assets WHERE id = {asset.Id};");
        });

        // Bị chặn bởi PostgreSQL Foreign Key Restrict constraint
        Assert.Contains("FK_asset_allocations_assets_asset_id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckConstraint_RentalPeriodOrder_EnforcedByPostgres()
    {
        await using var context = await CreateMigratedDbContextAsync();

        var assetId = Guid.NewGuid();
        var asset = InventoryAsset.Create(assetId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Sony FX3", 1, "{}", DateTimeOffset.UtcNow, AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", DateTimeOffset.UtcNow).Value).Value;
        context.Assets.Add(asset);
        await context.SaveChangesAsync();

        // Cố tình chèn allocation với start_date > end_date qua raw SQL để kiểm tra check constraint DB
        var invalidInsertSql = $"""
            INSERT INTO inventory.asset_allocations
            (id, asset_id, source_system, external_reference, status, start_date, end_date, created_at, updated_at)
            VALUES ('{Guid.NewGuid()}', '{assetId}', 'test', '{Guid.NewGuid():N}', 2, '2026-11-10', '2026-11-05', NOW(), NOW());
            """;

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync(invalidInsertSql));

        Assert.Equal("23514", ex.SqlState);
        Assert.Contains("ck_asset_allocations_rental_period_order", ex.ConstraintName);
    }

    [Fact]
    public async Task CheckConstraint_HeldExpiry_EnforcedByPostgres()
    {
        await using var context = await CreateMigratedDbContextAsync();

        var assetId = Guid.NewGuid();
        var asset = InventoryAsset.Create(assetId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Sony FX3", 1, "{}", DateTimeOffset.UtcNow, AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", DateTimeOffset.UtcNow).Value).Value;
        context.Assets.Add(asset);
        await context.SaveChangesAsync();

        // Chèn status Held (1) nhưng hold_expires_at = NULL
        var invalidInsertSql = $"""
            INSERT INTO inventory.asset_allocations
            (id, asset_id, source_system, external_reference, status, hold_expires_at, start_date, end_date, created_at, updated_at)
            VALUES ('{Guid.NewGuid()}', '{assetId}', 'test', '{Guid.NewGuid():N}', 1, NULL, '2026-11-01', '2026-11-05', NOW(), NOW());
            """;

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync(invalidInsertSql));

        Assert.Equal("23514", ex.SqlState);
        Assert.Contains("ck_asset_allocations_held_hold_expires_at", ex.ConstraintName);
    }

    [Fact]
    public async Task UniqueIndex_SourceSystemExternalReference_PreventsDuplicates()
    {
        await using var context = await CreateMigratedDbContextAsync();

        var assetId = Guid.NewGuid();
        var asset = InventoryAsset.Create(assetId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Sony FX3", 1, "{}", DateTimeOffset.UtcNow, AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", DateTimeOffset.UtcNow).Value).Value;
        context.Assets.Add(asset);
        await context.SaveChangesAsync();

        const string sourceSystem = "rental_service";
        var externalReference = $"REF_{Guid.NewGuid():N}";

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inventory.asset_allocations (id, asset_id, source_system, external_reference, status, start_date, end_date, created_at, updated_at)
            VALUES ({Guid.NewGuid()}, {assetId}, {sourceSystem}, {externalReference}, 2, '2026-11-01', '2026-11-05', NOW(), NOW());
            """);

        // Cố gắng chèn bản ghi thứ hai có cùng source_system và external_reference
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.asset_allocations (id, asset_id, source_system, external_reference, status, start_date, end_date, created_at, updated_at)
                VALUES ({Guid.NewGuid()}, {assetId}, {sourceSystem}, {externalReference}, 2, '2026-11-10', '2026-11-15', NOW(), NOW());
                """));

        Assert.Equal("23505", ex.SqlState);
        Assert.Contains("ix_asset_allocations_source_system_external_reference", ex.ConstraintName);
    }

    [Fact]
    public async Task ConcurrencyToken_Xmin_DetectsStaleUpdates()
    {
        await using var context1 = await CreateMigratedDbContextAsync();
        await using var context2 = await CreateMigratedDbContextAsync();

        var assetId = Guid.NewGuid();
        var asset = InventoryAsset.Create(assetId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Sony FX3", 1, "{}", DateTimeOffset.UtcNow, AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", DateTimeOffset.UtcNow).Value).Value;
        context1.Assets.Add(asset);
        await context1.SaveChangesAsync();

        var a1 = await context1.Assets.FindAsync(assetId);
        var a2 = await context2.Assets.FindAsync(assetId);

        Assert.NotNull(a1);
        Assert.NotNull(a2);

        a1.DisableBookings("Bảo trì", DateTimeOffset.UtcNow);
        await context1.SaveChangesAsync();

        a2.DisableBookings("Cập nhật khác", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context2.SaveChangesAsync());
    }

    [Fact]
    public async Task ScopedReads_EnforceOwnerIsolation()
    {
        using var sp = BuildServiceProvider();
        await using var db = await CreateMigratedDbContextAsync();

        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var org = Guid.NewGuid();
        var type = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var assetA = InventoryAsset.Create(aId, ownerA, type, Guid.NewGuid(), "Sony FX3", 1, "{}", now, AssetRentalRate.Create(aId, 100_000m, null, null, "VND", now).Value).Value;
        var assetB = InventoryAsset.Create(bId, ownerB, type, Guid.NewGuid(), "Sony A7S III", 1, "{}", now, AssetRentalRate.Create(bId, 100_000m, null, null, "VND", now).Value).Value;
        db.Assets.AddRange(assetA, assetB);
        await db.SaveChangesAsync();

        using var scope = sp.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IInventoryAssetRepository>();

        // Owner A chỉ thấy assetA
        var foundForOwnerA = await repo.GetByIdForOwnerAsync(assetA.Id, ownerA);
        Assert.NotNull(foundForOwnerA);

        // Owner B không thể đọc asset của Owner A dù cùng organizationId
        var notFoundForOwnerB = await repo.GetByIdForOwnerAsync(assetA.Id, ownerB);
        Assert.Null(notFoundForOwnerB);

        var listA = await repo.SearchAsync(AssetSearchCriteria.ForOwner(ownerA, 0, 10));
        Assert.Single(listA);
        Assert.Equal(assetA.Id, listA[0].Id);
    }
}
