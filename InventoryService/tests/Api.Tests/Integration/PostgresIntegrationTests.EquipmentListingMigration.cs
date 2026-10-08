using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class PostgresEquipmentListingMigrationTests
{
    private const string PreviousMigration = "20261006000000_AddAssetRentalRate";
    private const string TargetMigration = "20261007000000_RefactorEquipmentListingAndInventoryAsset";

    [Fact]
    public async Task ForwardMigration_EmptyDb_SucceedsAndCreatesEquipmentListings()
    {
        var fixture = new PostgresFixture { TargetMigration = PreviousMigration };
        await fixture.InitializeAsync();

        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;

            await using var db = new InventoryServiceDbContext(options);

            // Apply forward migration to RefactorEquipmentListingAndInventoryAsset
            await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

            // Verify equipment_listings table exists
            var listingsTableExists = await db.Database.SqlQueryRaw<bool>(
                "SELECT to_regclass('inventory.equipment_listings') IS NOT NULL AS \"Value\"").SingleAsync();
            Assert.True(listingsTableExists);

            // Verify asset_rental_rates table was dropped
            var rentalRatesTableExists = await db.Database.SqlQueryRaw<bool>(
                "SELECT to_regclass('inventory.asset_rental_rates') IS NOT NULL AS \"Value\"").SingleAsync();
            Assert.False(rentalRatesTableExists);

            // Verify assets table has equipment_listing_id and asset_code columns
            var hasListingFk = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'inventory' AND table_name = 'assets' AND column_name = 'equipment_listing_id'
                ) AS "Value"
            """).SingleAsync();
            Assert.True(hasListingFk);

            var hasAssetCode = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'inventory' AND table_name = 'assets' AND column_name = 'asset_code'
                ) AS "Value"
            """).SingleAsync();
            Assert.True(hasAssetCode);

            // Verify asset_allocations has group_reference column
            var hasGroupRef = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'inventory' AND table_name = 'asset_allocations' AND column_name = 'group_reference'
                ) AS "Value"
            """).SingleAsync();
            Assert.True(hasGroupRef);

            // Verify migration recorded in history
            var recorded = await db.Database.SqlQueryRaw<bool>($"""
                SELECT EXISTS (
                    SELECT 1 FROM inventory."__EFMigrationsHistory"
                    WHERE "MigrationId" = '{TargetMigration}'
                ) AS "Value"
            """).SingleAsync();
            Assert.True(recorded);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task ForwardMigration_PopulatedDatabase_RefusesAndRollsBack()
    {
        var fixture = new PostgresFixture { TargetMigration = PreviousMigration };
        await fixture.InitializeAsync();

        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;

            await using var db = new InventoryServiceDbContext(options);

            // Insert old asset directly in historical schema
            var assetId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var typeId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.assets (id, owner_user_id, equipment_type_id, model, schema_version, spec_values, accepting_bookings, created_at, updated_at)
                VALUES ({{assetId}}, {{ownerId}}, {{typeId}}, 'Legacy Camera', 1, '{"res":"4K"}'::jsonb, true, NOW(), NOW());

                INSERT INTO inventory.asset_rental_rates (asset_id, daily_rate, currency, created_at, updated_at)
                VALUES ({{assetId}}, 100000, 'VND', NOW(), NOW());
            """);

            // Migration must fail because database is not empty!
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                db.GetService<IMigrator>().MigrateAsync(TargetMigration));

            Assert.Contains("requires empty inventory tables", ex.Message);

            // Verify rollback: assets and asset_rental_rates still exist and contain the data!
            var assetCount = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM inventory.assets").SingleAsync();
            Assert.Equal(1, assetCount);
            var rateCount = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM inventory.asset_rental_rates").SingleAsync();
            Assert.Equal(1, rateCount);

            // Migration was NOT recorded in history
            var recorded = await db.Database.SqlQueryRaw<bool>($"""
                SELECT EXISTS (
                    SELECT 1 FROM inventory."__EFMigrationsHistory"
                    WHERE "MigrationId" = '{TargetMigration}'
                ) AS "Value"
            """).SingleAsync();
            Assert.False(recorded);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Downgrade_ThrowsNotSupportedException()
    {
        var fixture = new PostgresFixture { TargetMigration = TargetMigration };
        await fixture.InitializeAsync();

        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;

            await using var db = new InventoryServiceDbContext(options);

            var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
                db.GetService<IMigrator>().MigrateAsync(PreviousMigration));

            Assert.Contains("Down is blocked", ex.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task ForwardMigration_RequireSerial_SucceedsAndAppliesNotNullConstraintAndUniqueIndex()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261007000000_RefactorEquipmentListingAndInventoryAsset" };
        await fixture.InitializeAsync();

        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;

            await using var db = new InventoryServiceDbContext(options);

            // Apply migration
            await db.GetService<IMigrator>().MigrateAsync("20261008000000_RequireAssetSerialNumberAndUniqueListingSerial");

            // Verify serial_number is NOT NULL
            var isNullable = await db.Database.SqlQueryRaw<string>("""
                SELECT is_nullable AS "Value" FROM information_schema.columns
                WHERE table_schema = 'inventory' AND table_name = 'assets' AND column_name = 'serial_number'
            """).SingleAsync();
            Assert.Equal("NO", isNullable);

            // Verify unique index exists
            var indexExists = await db.Database.SqlQueryRaw<bool>("""
                SELECT to_regclass('inventory.ix_assets_listing_serial_unique') IS NOT NULL AS "Value"
            """).SingleAsync();
            Assert.True(indexExists);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task ForwardMigration_RequireSerial_PreflightRejectsNullOrDuplicateSerial()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261007000000_RefactorEquipmentListingAndInventoryAsset" };
        await fixture.InitializeAsync();

        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;

            await using var db = new InventoryServiceDbContext(options);

            // Insert listing and asset with NULL serial
            var listingId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.equipment_listings (id, owner_user_id, equipment_type_id, brand_id, model, schema_version, spec_values, daily_rate, currency, status, created_at, updated_at)
                VALUES ({{listingId}}, {{Guid.NewGuid()}}, {{Guid.NewGuid()}}, {{Guid.NewGuid()}}, 'Test', 1, '{}'::jsonb, 100000, 'VND', 1, NOW(), NOW());

                INSERT INTO inventory.assets (id, equipment_listing_id, asset_code, serial_number, accepting_bookings, created_at, updated_at)
                VALUES ({{assetId}}, {{listingId}}, 'AST-00000000000000000000000000000001', NULL, true, NOW(), NOW());
            """);

            // Migration must fail on preflight check!
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                db.GetService<IMigrator>().MigrateAsync("20261008000000_RequireAssetSerialNumberAndUniqueListingSerial"));

            Assert.Contains("requires all assets to have non-empty serial_number", ex.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
