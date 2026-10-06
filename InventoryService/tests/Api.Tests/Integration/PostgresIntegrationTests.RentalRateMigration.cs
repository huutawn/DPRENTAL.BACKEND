using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class PostgresIntegrationTests
{
    [Fact]
    public async Task AddAssetRentalRate_ForwardMigration_EmptyDb_SucceedsAndCreatesTableAndConstraints()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261005050000_AddAssetBrandId" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new InventoryServiceDbContext(options);

            await db.Database.MigrateAsync();

            // 1. Table exists
            var tableExists = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.tables
                    WHERE table_schema = 'inventory' AND table_name = 'asset_rental_rates'
                ) AS "Value"
            """).SingleAsync();
            Assert.True(tableExists);

            // 2. Index on daily_rate exists
            var indexExists = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_indexes
                    WHERE schemaname = 'inventory' AND tablename = 'asset_rental_rates' AND indexname = 'ix_asset_rental_rates_daily_rate'
                ) AS "Value"
            """).SingleAsync();
            Assert.True(indexExists);

            // 3. Constraints exist
            var constraints = await db.Database.SqlQueryRaw<string>("""
                SELECT constraint_name AS "Value" FROM information_schema.check_constraints
                WHERE constraint_schema = 'inventory' AND constraint_name LIKE 'ck_asset_rental_rates_%'
            """).ToListAsync();

            Assert.Contains("ck_asset_rental_rates_daily_rate", constraints);
            Assert.Contains("ck_asset_rental_rates_weekly_rate", constraints);
            Assert.Contains("ck_asset_rental_rates_monthly_rate", constraints);
            Assert.Contains("ck_asset_rental_rates_currency", constraints);
            Assert.Contains("ck_asset_rental_rates_asset_id", constraints);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddAssetRentalRate_ForwardMigration_EmptyAssets_StagingHasData_RollsBackAndRetainsStaging()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261005050000_AddAssetBrandId" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new InventoryServiceDbContext(options);

            // Bảng assets rỗng, nhưng staging có bản ghi (AssetId không tồn tại trong assets)
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS inventory.asset_rental_rate_backfill (
                    asset_id uuid PRIMARY KEY,
                    daily_rate numeric NOT NULL,
                    weekly_rate numeric,
                    monthly_rate numeric,
                    currency text NOT NULL DEFAULT 'VND'
                );
            """);

            var unmatchedId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.asset_rental_rate_backfill (asset_id, daily_rate, currency)
                VALUES ({unmatchedId}, 100000, 'VND');
            """);

            // Migration phải fail và rollback
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains("khong ton tai trong inventory.assets", ex.Message);

            // Bảng staging và dữ liệu của nó được giữ nguyên do transaction rollback
            var stagingExists = await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('inventory.asset_rental_rate_backfill') IS NOT NULL AS \"Value\"").SingleAsync();
            Assert.True(stagingExists);
            var stagingCount = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*)::integer AS \"Value\" FROM inventory.asset_rental_rate_backfill").SingleAsync();
            Assert.Equal(1, stagingCount);

            // Bảng target asset_rental_rates KHÔNG được tạo
            var tableExists = await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('inventory.asset_rental_rates') IS NOT NULL AS \"Value\"").SingleAsync();
            Assert.False(tableExists);

            // Migration history KHÔNG ghi nhận AddAssetRentalRate
            var historyRecorded = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM inventory."__EFMigrationsHistory"
                    WHERE "MigrationId" = '20261006000000_AddAssetRentalRate'
                ) AS "Value"
            """).SingleAsync();
            Assert.False(historyRecorded);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddAssetRentalRate_ForwardMigration_PopulatedWithValidBackfill_MigratesSuccessfully()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261005050000_AddAssetBrandId" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new InventoryServiceDbContext(options);

            var assetId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var eqId = Guid.NewGuid();
            var brandId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.assets (id, owner_user_id, equipment_type_id, brand_id, model, schema_version, spec_values, accepting_bookings, created_at, updated_at)
                VALUES ({{assetId}}, {{ownerId}}, {{eqId}}, {{brandId}}, 'Sony FX3', 1, '{"res":"4K"}'::jsonb, true, {{now}}, {{now}});
            """);

            // Tạo staging và insert rate
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS inventory.asset_rental_rate_backfill (
                    asset_id uuid PRIMARY KEY,
                    daily_rate numeric NOT NULL,
                    weekly_rate numeric,
                    monthly_rate numeric,
                    currency text NOT NULL DEFAULT 'VND'
                );
            """);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.asset_rental_rate_backfill (asset_id, daily_rate, weekly_rate, monthly_rate, currency)
                VALUES ({assetId}, 500000, 3000000, 10000000, 'VND');
            """);

            // Migrate forward
            await db.Database.MigrateAsync();

            // Verify in asset_rental_rates
            var daily = await db.Database.SqlQuery<decimal>($"""
                SELECT daily_rate AS "Value" FROM inventory.asset_rental_rates WHERE asset_id = {assetId}
            """).SingleAsync();
            Assert.Equal(500000m, daily);

            var weekly = await db.Database.SqlQuery<decimal?>($"""
                SELECT weekly_rate AS "Value" FROM inventory.asset_rental_rates WHERE asset_id = {assetId}
            """).SingleAsync();
            Assert.Equal(3000000m, weekly);

            // Staging table is dropped
            var stagingExists = await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('inventory.asset_rental_rate_backfill') IS NOT NULL AS \"Value\"").SingleAsync();
            Assert.False(stagingExists);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddAssetRentalRate_MissingBackfill_RollsBack()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261005050000_AddAssetBrandId" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new InventoryServiceDbContext(options);

            var assetId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var eqId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.assets (id, owner_user_id, equipment_type_id, brand_id, model, schema_version, spec_values, accepting_bookings, created_at, updated_at)
                VALUES ({{assetId}}, {{ownerId}}, {{eqId}}, NULL, 'Sony FX3', 1, '{}'::jsonb, true, {{now}}, {{now}});
            """);

            // Do not provide backfill -> migration must throw and rollback
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains("asset_rental_rate_backfill thieu gia", ex.Message);

            // Table asset_rental_rates was NOT created
            var tableExists = await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('inventory.asset_rental_rates') IS NOT NULL AS \"Value\"").SingleAsync();
            Assert.False(tableExists);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddAssetRentalRate_InvalidBackfillFractionOrCurrency_RollsBack()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261005050000_AddAssetBrandId" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new InventoryServiceDbContext(options);

            var assetId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.assets (id, owner_user_id, equipment_type_id, brand_id, model, schema_version, spec_values, accepting_bookings, created_at, updated_at)
                VALUES ({{assetId}}, {{Guid.NewGuid()}}, {{Guid.NewGuid()}}, NULL, 'Sony FX3', 1, '{}'::jsonb, true, {{now}}, {{now}});
            """);

            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS inventory.asset_rental_rate_backfill (
                    asset_id uuid PRIMARY KEY,
                    daily_rate numeric NOT NULL,
                    weekly_rate numeric,
                    monthly_rate numeric,
                    currency text NOT NULL DEFAULT 'VND'
                );
            """);

            // Insert rate with fraction
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.asset_rental_rate_backfill (asset_id, daily_rate, currency)
                VALUES ({assetId}, 100.5, 'VND');
            """);

            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains("asset_rental_rate_backfill co", ex.Message);

            var tableExists = await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('inventory.asset_rental_rates') IS NOT NULL AS \"Value\"").SingleAsync();
            Assert.False(tableExists);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddAssetRentalRate_UnmatchedAssetIdInBackfill_RollsBack()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261005050000_AddAssetBrandId" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new InventoryServiceDbContext(options);

            var assetId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.assets (id, owner_user_id, equipment_type_id, brand_id, model, schema_version, spec_values, accepting_bookings, created_at, updated_at)
                VALUES ({{assetId}}, {{Guid.NewGuid()}}, {{Guid.NewGuid()}}, NULL, 'Sony FX3', 1, '{}'::jsonb, true, {{now}}, {{now}});
            """);

            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS inventory.asset_rental_rate_backfill (
                    asset_id uuid PRIMARY KEY,
                    daily_rate numeric NOT NULL,
                    weekly_rate numeric,
                    monthly_rate numeric,
                    currency text NOT NULL DEFAULT 'VND'
                );
            """);

            // Insert matching rate AND an unmatched rate
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.asset_rental_rate_backfill (asset_id, daily_rate, currency)
                VALUES ({assetId}, 100000, 'VND'),
                       ({Guid.NewGuid()}, 200000, 'VND');
            """);

            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains("khong ton tai trong inventory.assets", ex.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddAssetRentalRate_Downgrade_ThrowsNotSupportedException()
    {
        await using var db = await CreateMigratedDbContextAsync();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            db.Database.MigrateAsync("20261005050000_AddAssetBrandId"));
        Assert.Contains("AddAssetRentalRate retains asset_rental_rates table", ex.Message);

        // Verify table still exists
        var tableExists = await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('inventory.asset_rental_rates') IS NOT NULL AS \"Value\"").SingleAsync();
        Assert.True(tableExists);
    }
}
