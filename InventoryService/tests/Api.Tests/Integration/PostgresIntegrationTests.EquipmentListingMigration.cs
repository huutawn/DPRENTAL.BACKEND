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
    public async Task ForwardMigration_RequireSerial_SucceedsAndAppliesNotNullConstraintAndUniqueIndex_AndDbRejectsDuplicates()
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

            // 1. Verify serial_number is NOT NULL
            var isNullable = await db.Database.SqlQueryRaw<string>("""
                SELECT is_nullable AS "Value" FROM information_schema.columns
                WHERE table_schema = 'inventory' AND table_name = 'assets' AND column_name = 'serial_number'
            """).SingleAsync();
            Assert.Equal("NO", isNullable);

            // 2. Verify unique index exists
            var indexExists = await db.Database.SqlQueryRaw<bool>("""
                SELECT to_regclass('inventory.ix_assets_listing_serial_unique') IS NOT NULL AS "Value"
            """).SingleAsync();
            Assert.True(indexExists);

            // 3. Insert valid listing and unit 1
            var listingId = Guid.NewGuid();
            var unit1Id = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.equipment_listings (id, owner_user_id, equipment_type_id, brand_id, model, schema_version, spec_values, daily_rate, currency, status, created_at, updated_at)
                VALUES ({{listingId}}, {{Guid.NewGuid()}}, {{Guid.NewGuid()}}, {{Guid.NewGuid()}}, 'Test Listing', 1, '{}'::jsonb, 100000, 'VND', 1, NOW(), NOW());

                INSERT INTO inventory.assets (id, equipment_listing_id, asset_code, serial_number, accepting_bookings, created_at, updated_at)
                VALUES ({{unit1Id}}, {{listingId}}, {{Guid.NewGuid().ToString("N").Insert(0, "AST-")}}, 'SN-UNIQUE-001', true, NOW(), NOW());
            """);

            // 4. DB actively rejects duplicate serial with different casing and whitespace
            var unit2Id = Guid.NewGuid();
            var unit2Code = Guid.NewGuid().ToString("N").Insert(0, "AST-");
            var dupEx = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO inventory.assets (id, equipment_listing_id, asset_code, serial_number, accepting_bookings, created_at, updated_at)
                    VALUES ({{unit2Id}}, {{listingId}}, {{unit2Code}}, '  sn-unique-001  ', true, NOW(), NOW());
                """));
            Assert.Equal("23505", dupEx.SqlState); // unique_violation

            // 5. DB actively rejects blank serial (check constraint)
            var unit3Id = Guid.NewGuid();
            var unit3Code = Guid.NewGuid().ToString("N").Insert(0, "AST-");
            var blankEx = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO inventory.assets (id, equipment_listing_id, asset_code, serial_number, accepting_bookings, created_at, updated_at)
                    VALUES ({{unit3Id}}, {{listingId}}, {{unit3Code}}, '   ', true, NOW(), NOW());
                """));
            Assert.Equal("23514", blankEx.SqlState); // check_violation
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task ForwardMigration_RequireSerial_PreflightRejectsNullOrBlankSerial_AndPreservesHistoryAndData()
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
                VALUES ({{listingId}}, {{Guid.NewGuid()}}, {{Guid.NewGuid()}}, {{Guid.NewGuid()}}, 'Test Listing', 1, '{}'::jsonb, 100000, 'VND', 1, NOW(), NOW());

                INSERT INTO inventory.assets (id, equipment_listing_id, asset_code, serial_number, accepting_bookings, created_at, updated_at)
                VALUES ({{assetId}}, {{listingId}}, 'AST-00000000000000000000000000000001', NULL, true, NOW(), NOW());
            """);

            // Migration must fail on preflight check!
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                db.GetService<IMigrator>().MigrateAsync("20261008000000_RequireAssetSerialNumberAndUniqueListingSerial"));

            Assert.Contains("requires all assets to have non-empty serial_number", ex.Message);

            // Migration history must NOT record the failed migration
            var historyRecorded = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM inventory."__EFMigrationsHistory"
                    WHERE "MigrationId" = '20261008000000_RequireAssetSerialNumberAndUniqueListingSerial'
                ) AS "Value"
            """).SingleAsync();
            Assert.False(historyRecorded);

            // Existing data remains untouched
            var assetStillExists = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS (
                    SELECT 1 FROM inventory.assets WHERE id = {assetId}
                ) AS "Value"
            """).SingleAsync();
            Assert.True(assetStillExists);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task ForwardMigration_RequireSerial_PreflightRejectsDuplicateCasingOrTrim_AndPreservesHistoryAndData()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261007000000_RefactorEquipmentListingAndInventoryAsset" };
        await fixture.InitializeAsync();

        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;

            await using var db = new InventoryServiceDbContext(options);

            // Insert listing with 2 assets having duplicate serial after trim/lowercase
            var listingId = Guid.NewGuid();
            var a1 = Guid.NewGuid();
            var a2 = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.equipment_listings (id, owner_user_id, equipment_type_id, brand_id, model, schema_version, spec_values, daily_rate, currency, status, created_at, updated_at)
                VALUES ({{listingId}}, {{Guid.NewGuid()}}, {{Guid.NewGuid()}}, {{Guid.NewGuid()}}, 'Test Listing', 1, '{}'::jsonb, 100000, 'VND', 1, NOW(), NOW());

                INSERT INTO inventory.assets (id, equipment_listing_id, asset_code, serial_number, accepting_bookings, created_at, updated_at)
                VALUES ({{a1}}, {{listingId}}, 'AST-00000000000000000000000000000001', 'FX3-SN-001', true, NOW(), NOW());

                INSERT INTO inventory.assets (id, equipment_listing_id, asset_code, serial_number, accepting_bookings, created_at, updated_at)
                VALUES ({{a2}}, {{listingId}}, 'AST-00000000000000000000000000000002', '  fx3-sn-001  ', true, NOW(), NOW());
            """);

            // Migration must fail on preflight duplicate check!
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                db.GetService<IMigrator>().MigrateAsync("20261008000000_RequireAssetSerialNumberAndUniqueListingSerial"));

            Assert.Contains("detected duplicate serial numbers within the same equipment listing", ex.Message);

            // Migration history must NOT record the failed migration
            var historyRecorded = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM inventory."__EFMigrationsHistory"
                    WHERE "MigrationId" = '20261008000000_RequireAssetSerialNumberAndUniqueListingSerial'
                ) AS "Value"
            """).SingleAsync();
            Assert.False(historyRecorded);

            // Data remains intact
            var count = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM inventory.assets WHERE equipment_listing_id = {listingId}
            """).SingleAsync();
            Assert.Equal(2, count);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
