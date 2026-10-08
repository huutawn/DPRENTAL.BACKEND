using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class PostgresIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalCompletedMigrationRequiresAuditedBackfill(bool supplyBackfill)
    {
        var fixture = new PostgresFixture { TargetMigration = "20261001035544_InitialInventoryBaseline" };
        await fixture.InitializeAsync();
        try
        {
            await using var db = new InventoryServiceDbContext(new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(fixture.ConnectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName)).Options);
            var assetId = Guid.NewGuid();
            var reservationId = Guid.NewGuid();
            var mediaId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inventory.equipments(id,organization_id,catalog_lifecycle_status,accepting_bookings,created_at,updated_at) VALUES({assetId},{Guid.NewGuid()},1,true,NOW(),NOW())");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inventory.reservations(id,equipment_id,source_type,status,start_date,end_date,delivered_at,delivered_by,returned_at,returned_by,return_condition,delivery_photo_ids,return_photo_ids,created_at,updated_at) VALUES({reservationId},{assetId},1,4,'2026-11-01','2026-11-02',NOW()-INTERVAL '1 hour','staff',NOW(),'staff','Original damage evidence',ARRAY[]::uuid[],ARRAY[{mediaId}],NOW(),NOW())");
            var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            if (supplyBackfill)
            {
                await db.Database.ExecuteSqlRawAsync("CREATE TABLE inventory.return_condition_backfill(reservation_id uuid PRIMARY KEY,condition integer NOT NULL CHECK(condition IN (1,2)))");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inventory.return_condition_backfill VALUES({reservationId},2)");
                await migrator.MigrateAsync("20261001043209_HardenInventoryContracts");
                var condition = await db.Database.SqlQuery<int>($"SELECT return_condition AS \"Value\" FROM inventory.reservations WHERE id={reservationId}").SingleAsync();
                Assert.Equal(2, condition);
                var evidence = await db.Database.SqlQuery<string>($"SELECT return_condition_notes AS \"Value\" FROM inventory.reservations WHERE id={reservationId}").SingleAsync();
                Assert.Equal("Original damage evidence", evidence);
                Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('inventory.return_condition_backfill') IS NULL AS \"Value\"").SingleAsync());
            }
            else
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20261001043209_HardenInventoryContracts"));
                Assert.Contains("audited", error.Message);
                Assert.Single(await db.Database.GetAppliedMigrationsAsync());
            }
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task HardenBoundary_UpgradesPopulatedRefactoredDatabaseAndRemovesLegacyText()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261002011635_RefactorInventoryBoundary" };
        await fixture.InitializeAsync();
        try
        {
            await using var db = new InventoryServiceDbContext(new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(fixture.ConnectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName)).Options);
            var assetId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inventory.assets(id,owner_user_id,organization_id,equipment_type_id,schema_version,spec_values,accepting_bookings,created_at,updated_at) VALUES({assetId},{Guid.NewGuid()},{Guid.NewGuid()},{Guid.NewGuid()},1,'{{}}'::jsonb,true,NOW(),NOW())");
            var allocationId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inventory.asset_allocations(id,asset_id,source_system,external_reference,status,start_date,end_date,created_at,updated_at,notes) VALUES({allocationId},{assetId},'inventory.manual',{allocationId.ToString()},6,'2026-11-01','2026-11-02',NOW(),NOW(),'Customer name and phone')");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inventory.allocation_transitions(id,allocation_id,sequence,operation_type,from_status,to_status,created_at,actor,reason,operation_key,request_fingerprint) VALUES({Guid.NewGuid()},{allocationId},1,7,2,6,NOW(),'staff','Hoàn tất kỳ phân bổ','return-before-cutover',{new string('A', 64)})");
            await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20261002024109_HardenInventoryBoundary");
            var note = await db.Database.SqlQueryRaw<string?>("SELECT notes AS \"Value\" FROM inventory.asset_allocations").SingleAsync();
            var transition = await db.Database.SqlQueryRaw<string?>("SELECT operation_key AS \"Value\" FROM inventory.allocation_transitions").SingleAsync();
            var transitionReason = await db.Database.SqlQueryRaw<string?>("SELECT reason AS \"Value\" FROM inventory.allocation_transitions").SingleAsync();
            var transitionFp = await db.Database.SqlQueryRaw<string?>("SELECT request_fingerprint AS \"Value\" FROM inventory.allocation_transitions").SingleAsync();
            Assert.Null(note);
            Assert.Null(transitionReason);
            Assert.Null(transitionFp);
            Assert.Equal("return-before-cutover", transition);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inventory.assets SET spec_values='[]'::jsonb WHERE id={assetId}"));
            Assert.Equal("ck_assets_spec_values_object", ex.ConstraintName);
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM inventory.asset_allocations").SingleAsync());
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Theory]
    [InlineData("[]", 1)]
    [InlineData("{}", 0)]
    public async Task HardenBoundary_InvalidExistingAssetRollsBackWithoutDeletingText(string specs, int version)
    {
        var fixture = new PostgresFixture { TargetMigration = "20261002011635_RefactorInventoryBoundary" };
        await fixture.InitializeAsync();
        try
        {
            await using var db = new InventoryServiceDbContext(new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(fixture.ConnectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName)).Options);
            var assetId = Guid.NewGuid();
            var allocationId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inventory.assets(id,owner_user_id,organization_id,equipment_type_id,schema_version,spec_values,accepting_bookings,created_at,updated_at) VALUES({assetId},{Guid.NewGuid()},{Guid.NewGuid()},{Guid.NewGuid()},{version},{specs}::jsonb,true,NOW(),NOW())");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inventory.asset_allocations(id,asset_id,source_system,external_reference,status,start_date,end_date,created_at,updated_at,notes) VALUES({allocationId},{assetId},'inventory.manual',{allocationId.ToString()},6,'2026-11-01','2026-11-02',NOW(),NOW(),'original note')");
            var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20261002024109_HardenInventoryBoundary"));
            Assert.Equal(4, (await db.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*)::integer AS \"Value\" FROM inventory.assets").SingleAsync());
            Assert.Equal("original note", await db.Database.SqlQueryRaw<string>("SELECT notes AS \"Value\" FROM inventory.asset_allocations").SingleAsync());
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task ForwardMigration_PopulatedUpgrade_MigratesAllEntitiesAndStatusesSuccessfully()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261001080639_AddKafkaFoundationAndSagaRuntime" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(fixture.ConnectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName)).Options;
            await using var db = new InventoryServiceDbContext(options);

            var equipmentId = Guid.NewGuid();
            var orgId = Guid.NewGuid();
            var ownerUserId = Guid.NewGuid();
            var equipmentTypeId = Guid.NewGuid();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            // 1. Seed raw SQL: Equipments
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.equipments (id, organization_id, catalog_lifecycle_status, accepting_bookings, suspension_reason, created_at, updated_at)
                VALUES ({equipmentId}, {orgId}, 1, true, NULL, NOW(), NOW());
                """);

            // 2. Seed raw SQL: 6 reservations representing all 6 legacy statuses
            var resHeld = Guid.NewGuid();
            var resConfirmed = Guid.NewGuid();
            var resInUse = Guid.NewGuid();
            var resCompleted = Guid.NewGuid();
            var resCancelled = Guid.NewGuid();
            var resReleased = Guid.NewGuid();

            var start0 = today; var end0 = today.AddDays(2);
            var start1 = today.AddDays(3); var end1 = today.AddDays(5);
            var start2 = today.AddDays(6); var end2 = today.AddDays(8);
            var start3 = today.AddDays(9); var end3 = today.AddDays(11);
            var start4 = today.AddDays(12); var end4 = today.AddDays(14);
            var start5 = today.AddDays(15); var end5 = today.AddDays(17);

            var renterHeldId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.reservations (id, equipment_id, source_type, status, hold_expires_at, start_date, end_date, created_at, updated_at, delivery_photo_ids, return_photo_ids, source_system, external_reference, renter_account_id)
                VALUES ({resHeld}, {equipmentId}, 2, 1, NOW() + INTERVAL '2 hours', {start0}, {end0}, NOW(), NOW(), ARRAY[]::uuid[], ARRAY[]::uuid[], 'rental_service', 'REF-HELD-001', {renterHeldId});
                """);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.reservations (id, equipment_id, source_type, status, start_date, end_date, created_at, updated_at, manual_renter_name, manual_renter_phone, delivery_photo_ids, return_photo_ids)
                VALUES ({resConfirmed}, {equipmentId}, 1, 2, {start1}, {end1}, NOW(), NOW(), 'Renter Confirmed', '0901234567', ARRAY[]::uuid[], ARRAY[]::uuid[]);
                """);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.reservations (id, equipment_id, source_type, status, start_date, end_date, delivered_at, delivered_by, created_at, updated_at, manual_renter_name, manual_renter_phone, delivery_photo_ids, return_photo_ids)
                VALUES ({resInUse}, {equipmentId}, 1, 3, {start2}, {end2}, NOW(), 'staff', NOW(), NOW(), 'Renter InUse', '0901234567', ARRAY[]::uuid[], ARRAY[]::uuid[]);
                """);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.reservations (id, equipment_id, source_type, status, start_date, end_date, delivered_at, delivered_by, returned_at, returned_by, return_condition, created_at, updated_at, manual_renter_name, manual_renter_phone, delivery_photo_ids, return_photo_ids)
                VALUES ({resCompleted}, {equipmentId}, 1, 4, {start3}, {end3}, NOW() - INTERVAL '1 day', 'staff', NOW(), 'staff', 1, NOW(), NOW(), 'Renter Completed', '0901234567', ARRAY[]::uuid[], ARRAY[]::uuid[]);
                """);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.reservations (id, equipment_id, source_type, status, start_date, end_date, created_at, updated_at, manual_renter_name, manual_renter_phone, delivery_photo_ids, return_photo_ids)
                VALUES ({resCancelled}, {equipmentId}, 1, 5, {start4}, {end4}, NOW(), NOW(), 'Renter Cancelled', '0901234567', ARRAY[]::uuid[], ARRAY[]::uuid[]);
                """);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.reservations (id, equipment_id, source_type, status, start_date, end_date, created_at, updated_at, manual_renter_name, manual_renter_phone, delivery_photo_ids, return_photo_ids)
                VALUES ({resReleased}, {equipmentId}, 1, 6, {start5}, {end5}, NOW(), NOW(), 'Renter Released', '0901234567', ARRAY[]::uuid[], ARRAY[]::uuid[]);
                """);

            // 3. Seed transitions: Deliver (7) on InUse and Return (8) on Completed
            var trans1 = Guid.NewGuid();
            var trans2 = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.reservation_transitions (id, reservation_id, sequence, operation_type, from_status, to_status, created_at, actor)
                VALUES ({trans1}, {resInUse}, 1, 7, 2, 3, NOW(), 'staff');
                """);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.reservation_transitions (id, reservation_id, sequence, operation_type, from_status, to_status, created_at, actor)
                VALUES ({trans2}, {resCompleted}, 1, 8, 3, 4, NOW(), 'staff');
                """);

            // 4. Seed CalendarBlock
            var blockId = Guid.NewGuid();
            var blockStart = today.AddDays(20);
            var blockEnd = today.AddDays(22);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.calendar_blocks (id, equipment_id, start_date, end_date, reason, created_by, created_at)
                VALUES ({blockId}, {equipmentId}, {blockStart}, {blockEnd}, 1, 'op', NOW());
                """);

            // 5. Seed DamageReport
            var damageId = Guid.NewGuid();
            var reporterId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.damage_reports (id, reservation_id, reporter_account_id, description, is_usable, photo_media_ids, created_at)
                VALUES ({damageId}, {resCompleted}, {reporterId}, 'Mô tả hư hỏng thiết bị hợp lệ', false, ARRAY[]::uuid[], NOW());
                """);

            // 6. Seed Notification
            var notifId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.notifications (id, organization_id, deduplication_key, notification_type, content, expires_at, created_at)
                VALUES ({notifId}, {orgId}, 'DEDUP-001', 1, 'Nội dung thông báo', NOW() + INTERVAL '7 days', NOW());
                """);

            // 7. Seed SagaInstance
            var sagaId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.saga_instances (id, saga_type, organization_id, correlation_id, state, current_step, data, created_at, updated_at)
                VALUES ({{sagaId}}, 'OrderSaga', {{orgId}}, 'CORR-001', 1, 'Start', '{}', NOW(), NOW());
                """);

            // 8. Seed Inbox and Outbox
            var outboxId = Guid.NewGuid();
            var inboxId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.outbox_messages (id, topic, kafka_key, payload, retry_count, created_at)
                VALUES ({{outboxId}}, 'legacy.topic', 'KEY-1', '{}', 0, NOW());
                """);

            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.inbox_messages (id, message_id, handler, received_at, status, retry_count)
                VALUES ({{inboxId}}, 'MSG-001', 'TestHandler', NOW(), 1, 0);
                """);
            // 9. Chuẩn bị bảng staging asset_backfill
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS inventory.asset_backfill (
                    asset_id uuid PRIMARY KEY,
                    owner_user_id uuid NOT NULL,
                    equipment_type_id uuid NOT NULL,
                    schema_version integer NOT NULL CHECK (schema_version >= 1),
                    spec_values jsonb NOT NULL
                );
                """);

            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.asset_backfill (asset_id, owner_user_id, equipment_type_id, schema_version, spec_values)
                VALUES ({{equipmentId}}, {{ownerUserId}}, {{equipmentTypeId}}, 1, '{"model":"Sony FX3"}'::jsonb);
                """);

            // 10. Chạy forward migration RefactorInventoryBoundary
            await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20261002011635_RefactorInventoryBoundary");

            // 11. Kiểm tra kết quả migration:
            var assetRow = await db.Database.SqlQuery<string>($"""
                SELECT spec_values::text AS "Value" FROM inventory.assets WHERE id = {equipmentId} AND owner_user_id = {ownerUserId} AND equipment_type_id = {equipmentTypeId} AND accepting_bookings = true
                """).SingleAsync();
            Assert.Contains("Sony FX3", assetRow);

            // Allocations mapping status:
            var statusHeld = await db.Database.SqlQuery<int>($"SELECT status AS \"Value\" FROM inventory.asset_allocations WHERE id = {resHeld}").SingleAsync();
            Assert.Equal(1, statusHeld);

            var statusConfirmed = await db.Database.SqlQuery<int>($"SELECT status AS \"Value\" FROM inventory.asset_allocations WHERE id = {resConfirmed}").SingleAsync();
            Assert.Equal(2, statusConfirmed);

            var statusInUse = await db.Database.SqlQuery<int>($"SELECT status AS \"Value\" FROM inventory.asset_allocations WHERE id = {resInUse}").SingleAsync();
            Assert.Equal(2, statusInUse);

            var statusCompleted = await db.Database.SqlQuery<int>($"SELECT status AS \"Value\" FROM inventory.asset_allocations WHERE id = {resCompleted}").SingleAsync();
            Assert.Equal(6, statusCompleted);

            var statusCancelled = await db.Database.SqlQuery<int>($"SELECT status AS \"Value\" FROM inventory.asset_allocations WHERE id = {resCancelled}").SingleAsync();
            Assert.Equal(5, statusCancelled);

            var statusReleased = await db.Database.SqlQuery<int>($"SELECT status AS \"Value\" FROM inventory.asset_allocations WHERE id = {resReleased}").SingleAsync();
            Assert.Equal(6, statusReleased);

            // Manual allocation source_system & external_reference normalized:
            var sourceSystem = await db.Database.SqlQuery<string>($"SELECT source_system AS \"Value\" FROM inventory.asset_allocations WHERE id = {resConfirmed}").SingleAsync();
            Assert.Equal("inventory.manual", sourceSystem);
            var extRef = await db.Database.SqlQuery<string>($"SELECT external_reference AS \"Value\" FROM inventory.asset_allocations WHERE id = {resConfirmed}").SingleAsync();
            Assert.Equal(resConfirmed.ToString("D"), extRef);
            // Transitions: Deliver (7) was dropped, Return (8) became Release (7)
            var deliverTransitions = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*)::integer AS \"Value\" FROM inventory.allocation_transitions WHERE operation_type = 7 AND to_status = 2").SingleAsync();
            Assert.Equal(0, deliverTransitions);
            var releaseTransitions = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*)::integer AS \"Value\" FROM inventory.allocation_transitions WHERE operation_type = 7 AND to_status = 6").SingleAsync();
            Assert.Equal(1, releaseTransitions);

            // Obsolete tables dropped:
            var notificationsExist = await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('inventory.notifications') IS NOT NULL AS \"Value\"").SingleAsync();
            Assert.False(notificationsExist);
            var damagesExist = await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('inventory.damage_reports') IS NOT NULL AS \"Value\"").SingleAsync();
            Assert.False(damagesExist);
            var sagasExist = await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('inventory.saga_instances') IS NOT NULL AS \"Value\"").SingleAsync();
            Assert.False(sagasExist);
            var stagingExists = await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('inventory.asset_backfill') IS NOT NULL AS \"Value\"").SingleAsync();
            Assert.False(stagingExists);

            var outboxCount = await db.Database.SqlQuery<int>($"SELECT COUNT(*)::integer AS \"Value\" FROM inventory.outbox_messages WHERE id = {outboxId}").SingleAsync();
            Assert.Equal(1, outboxCount);
            var inboxCount = await db.Database.SqlQuery<int>($"SELECT COUNT(*)::integer AS \"Value\" FROM inventory.inbox_messages WHERE id = {inboxId}").SingleAsync();
            Assert.Equal(1, inboxCount);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task ForwardMigration_MissingBackfill_RollsBackAndPreservesLegacyData()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261001080639_AddKafkaFoundationAndSagaRuntime" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(fixture.ConnectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName)).Options;
            await using var db = new InventoryServiceDbContext(options);

            var equipmentId = Guid.NewGuid();
            var orgId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.equipments (id, organization_id, catalog_lifecycle_status, accepting_bookings, suspension_reason, created_at, updated_at)
                VALUES ({equipmentId}, {orgId}, 1, true, NULL, NOW(), NOW());
                """);

            // Không tạo hoặc không seed asset_backfill cho equipmentId
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains("asset_backfill thieu hoac khong hop le", ex.Message);

            var legacyCount = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::integer AS "Value" FROM inventory.equipments WHERE id = {equipmentId}
                """).SingleAsync();
            Assert.Equal(1, legacyCount);

            // Chỉ 3 migrations được apply
            var applied = await db.Database.GetAppliedMigrationsAsync();
            Assert.Equal(3, applied.Count());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task CompleteAssetManagement_PopulatedUpgradeWithBackfill_MigratesModelAndDropsOrganization()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261002024109_HardenInventoryBoundary" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new InventoryServiceDbContext(options);

            var assetId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var orgId = Guid.NewGuid();
            var equipmentTypeId = Guid.NewGuid();

            // Seed asset với schema cũ (có organization_id, chưa có model)
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.assets (id, owner_user_id, organization_id, equipment_type_id, schema_version, spec_values, accepting_bookings, suspension_reason, created_at, updated_at)
                VALUES ({{assetId}}, {{ownerId}}, {{orgId}}, {{equipmentTypeId}}, 1, '{"lens":"24-70mm"}'::jsonb, true, NULL, NOW(), NOW());
                """);

            // Chuẩn bị bảng staging asset_model_backfill
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS inventory.asset_model_backfill (
                    asset_id uuid PRIMARY KEY,
                    model character varying(200) NOT NULL,
                    serial_number character varying(100),
                    manufacture_year integer,
                    thumbnail_media_id uuid
                );
                """);

            var thumbId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.asset_model_backfill (asset_id, model, serial_number, manufacture_year, thumbnail_media_id)
                VALUES ({assetId}, 'Sony FX3 Cinema Line', 'SN-98765', 2024, {thumbId});
                """);

            // Thực hiện forward migration sang CompleteInventoryAssetManagement
            await db.Database.MigrateAsync("20261002030000_CompleteInventoryAssetManagement");

            // Kiểm tra kết quả migration:
            var row = await db.Database.SqlQuery<string>($"""
                SELECT model AS "Value" FROM inventory.assets WHERE id = {assetId}
                """).SingleAsync();
            Assert.Equal("Sony FX3 Cinema Line", row);

            var serialRow = await db.Database.SqlQuery<string>($"""
                SELECT serial_number AS "Value" FROM inventory.assets WHERE id = {assetId}
                """).SingleAsync();
            Assert.Equal("SN-98765", serialRow);

            var yearRow = await db.Database.SqlQuery<int>($"""
                SELECT manufacture_year AS "Value" FROM inventory.assets WHERE id = {assetId}
                """).SingleAsync();
            Assert.Equal(2024, yearRow);

            var thumbRow = await db.Database.SqlQuery<Guid>($"""
                SELECT thumbnail_media_id AS "Value" FROM inventory.assets WHERE id = {assetId}
                """).SingleAsync();
            Assert.Equal(thumbId, thumbRow);

            // Bảng staging asset_model_backfill đã bị drop
            var stagingExists = await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('inventory.asset_model_backfill') IS NOT NULL AS \"Value\"").SingleAsync();
            Assert.False(stagingExists);

            // Cột organization_id không còn tồn tại trên bảng assets
            var orgColumnExists = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'inventory' AND table_name = 'assets' AND column_name = 'organization_id'
                ) AS "Value"
                """).SingleAsync();
            Assert.False(orgColumnExists);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task CompleteAssetManagement_MissingOrInvalidBackfill_RollsBack()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261002024109_HardenInventoryBoundary" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new InventoryServiceDbContext(options);

            var assetId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var orgId = Guid.NewGuid();
            var equipmentTypeId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.assets (id, owner_user_id, organization_id, equipment_type_id, schema_version, spec_values, accepting_bookings, suspension_reason, created_at, updated_at)
                VALUES ({{assetId}}, {{ownerId}}, {{orgId}}, {{equipmentTypeId}}, 1, '{}'::jsonb, true, NULL, NOW(), NOW());
                """);

            // Không có backfill hợp lệ -> migration phải fail và rollback
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains("asset_model_backfill thieu hoac khong hop le", ex.Message);

            // Xác nhận asset vẫn còn nguyên vẹn trên schema cũ
            var assetCount = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::integer AS "Value" FROM inventory.assets WHERE id = {assetId}
                """).SingleAsync();
            Assert.Equal(1, assetCount);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task CompleteAssetManagement_FutureYearBackfill_RollsBack()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261002024109_HardenInventoryBoundary" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new InventoryServiceDbContext(options);

            var assetId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var orgId = Guid.NewGuid();
            var equipmentTypeId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.assets (id, owner_user_id, organization_id, equipment_type_id, schema_version, spec_values, accepting_bookings, suspension_reason, created_at, updated_at)
                VALUES ({{assetId}}, {{ownerId}}, {{orgId}}, {{equipmentTypeId}}, 1, '{}'::jsonb, true, NULL, NOW(), NOW());
                """);

            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS inventory.asset_model_backfill (
                    asset_id uuid PRIMARY KEY,
                    model character varying(200) NOT NULL,
                    serial_number character varying(100),
                    manufacture_year integer,
                    thumbnail_media_id uuid
                );
                """);

            // Seed năm 2099 (năm tương lai) -> migration phải từ chối và rollback
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.asset_model_backfill (asset_id, model, manufacture_year)
                VALUES ({assetId}, 'Future Model', 2099);
                """);

            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains("asset_model_backfill thieu hoac khong hop le", ex.Message);

            // Asset vẫn nguyên vẹn
            var assetCount = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::integer AS "Value" FROM inventory.assets WHERE id = {assetId}
                """).SingleAsync();
            Assert.Equal(1, assetCount);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task CompleteAssetManagement_WhitespaceOnlyModelBackfill_RollsBack()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261002024109_HardenInventoryBoundary" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new InventoryServiceDbContext(options);

            var assetId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var orgId = Guid.NewGuid();
            var equipmentTypeId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.assets (id, owner_user_id, organization_id, equipment_type_id, schema_version, spec_values, accepting_bookings, suspension_reason, created_at, updated_at)
                VALUES ({{assetId}}, {{ownerId}}, {{orgId}}, {{equipmentTypeId}}, 1, '{}'::jsonb, true, NULL, NOW(), NOW());
                """);

            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS inventory.asset_model_backfill (
                    asset_id uuid PRIMARY KEY,
                    model character varying(200) NOT NULL,
                    serial_number character varying(100),
                    manufacture_year integer,
                    thumbnail_media_id uuid
                );
                """);

            // Seed model chỉ gồm tab, newline và space -> migration phải từ chối và rollback
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.asset_model_backfill (asset_id, model)
                VALUES ({assetId}, E'\t  \r\n  ');
                """);

            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains("asset_model_backfill thieu hoac khong hop le", ex.Message);

            // Asset vẫn nguyên vẹn
            var assetCount = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::integer AS "Value" FROM inventory.assets WHERE id = {assetId}
                """).SingleAsync();
            Assert.Equal(1, assetCount);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddAssetBrandId_ForwardMigration_EmptyDb_SucceedsAndAppliesNullableColumnAndCheckConstraint()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261002030000_CompleteInventoryAssetManagement" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new InventoryServiceDbContext(options);

            // Migrate forward to AddAssetBrandId
            await db.GetService<IMigrator>().MigrateAsync("20261005050000_AddAssetBrandId");

            // Verify brand_id column exists and is nullable
            var isNullable = await db.Database.SqlQueryRaw<string>("""
                SELECT is_nullable AS "Value" FROM information_schema.columns
                WHERE table_schema = 'inventory' AND table_name = 'assets' AND column_name = 'brand_id'
            """).SingleAsync();
            Assert.Equal("YES", isNullable);

            // Verify ck_assets_brand_id constraint exists
            var constraintExists = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.check_constraints
                    WHERE constraint_schema = 'inventory' AND constraint_name = 'ck_assets_brand_id'
                ) AS "Value"
            """).SingleAsync();
            Assert.True(constraintExists);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddAssetBrandId_ForwardMigration_ExistingAssets_RetainsDataAndBrandIdNull()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261002030000_CompleteInventoryAssetManagement" };
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
            var thumbId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.assets (id, owner_user_id, equipment_type_id, model, serial_number, manufacture_year, thumbnail_media_id, schema_version, spec_values, accepting_bookings, suspension_reason, created_at, updated_at)
                VALUES ({{assetId}}, {{ownerId}}, {{eqId}}, 'Legacy Camera', 'SN-LEGACY-01', 2023, {{thumbId}}, 1, '{"lens":"24-70"}'::jsonb, true, NULL, {{now}}, {{now}});
            """);

            // Forward migrate to AddAssetBrandId
            await db.Database.MigrateAsync("20261005050000_AddAssetBrandId");

            // Asset still exists with brand_id = NULL and all original fields intact
            var brandIdVal = await db.Database.SqlQuery<Guid?>($"""
                SELECT brand_id AS "Value" FROM inventory.assets WHERE id = {assetId}
            """).SingleAsync();
            Assert.Null(brandIdVal);

            var model = await db.Database.SqlQuery<string>($"""
                SELECT model AS "Value" FROM inventory.assets WHERE id = {assetId}
            """).SingleAsync();
            Assert.Equal("Legacy Camera", model);

            var serial = await db.Database.SqlQuery<string>($"""
                SELECT serial_number AS "Value" FROM inventory.assets WHERE id = {assetId}
            """).SingleAsync();
            Assert.Equal("SN-LEGACY-01", serial);

            var year = await db.Database.SqlQuery<int>($"""
                SELECT manufacture_year AS "Value" FROM inventory.assets WHERE id = {assetId}
            """).SingleAsync();
            Assert.Equal(2023, year);

            var thumb = await db.Database.SqlQuery<Guid>($"""
                SELECT thumbnail_media_id AS "Value" FROM inventory.assets WHERE id = {assetId}
            """).SingleAsync();
            Assert.Equal(thumbId, thumb);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddAssetBrandId_Constraint_RejectsEmptyGuid_AllowsNullAndValidGuid()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261005050000_AddAssetBrandId" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new InventoryServiceDbContext(options);
            var ownerId = Guid.NewGuid();
            var eqId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            // 1. Rejects empty GUID
            var invalidAssetId = Guid.NewGuid();
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO inventory.assets (id, owner_user_id, equipment_type_id, brand_id, model, schema_version, spec_values, accepting_bookings, created_at, updated_at)
            VALUES ({{invalidAssetId}}, {{ownerId}}, {{eqId}}, '00000000-0000-0000-0000-000000000000'::uuid, 'Test Model', 1, '{}'::jsonb, true, {{now}}, {{now}});
        """));
            Assert.Equal("ck_assets_brand_id", ex.ConstraintName);

            // 2. Allows NULL
            var nullBrandAssetId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO inventory.assets (id, owner_user_id, equipment_type_id, brand_id, model, schema_version, spec_values, accepting_bookings, created_at, updated_at)
            VALUES ({{nullBrandAssetId}}, {{ownerId}}, {{eqId}}, NULL, 'Test Model', 1, '{}'::jsonb, true, {{now}}, {{now}});
        """);
            var reloadedNull = await db.Database.SqlQuery<Guid?>($"""
            SELECT brand_id AS "Value" FROM inventory.assets WHERE id = {nullBrandAssetId}
        """).SingleAsync();
            Assert.Null(reloadedNull);

            // 3. Allows valid GUID
            var validBrand = Guid.NewGuid();
            var validAssetId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO inventory.assets (id, owner_user_id, equipment_type_id, brand_id, model, schema_version, spec_values, accepting_bookings, created_at, updated_at)
            VALUES ({{validAssetId}}, {{ownerId}}, {{eqId}}, {{validBrand}}, 'Test Model', 1, '{}'::jsonb, true, {{now}}, {{now}});
        """);
            var reloadedValid = await db.Database.SqlQuery<Guid?>($"""
            SELECT brand_id AS "Value" FROM inventory.assets WHERE id = {validAssetId}
        """).SingleAsync();
            Assert.Equal(validBrand, reloadedValid);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddAssetBrandId_Downgrade_EmptyDb_ThrowsNotSupportedException_AndPreservesSchemaAndHistory()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261005050000_AddAssetBrandId" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new InventoryServiceDbContext(options);

            // Attempt to downgrade to previous migration
            var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
                db.Database.MigrateAsync("20261002030000_CompleteInventoryAssetManagement"));
            Assert.Contains("AddAssetBrandId retains brand_id column and constraint", ex.Message);

            // Verify column brand_id still exists
            var colExists = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'inventory' AND table_name = 'assets' AND column_name = 'brand_id'
                ) AS "Value"
            """).SingleAsync();
            Assert.True(colExists);

            // Verify constraint ck_assets_brand_id still exists
            var constraintExists = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.check_constraints
                    WHERE constraint_schema = 'inventory' AND constraint_name = 'ck_assets_brand_id'
                ) AS "Value"
            """).SingleAsync();
            Assert.True(constraintExists);

            // Verify migration history still records AddAssetBrandId
            var historyExists = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM inventory."__EFMigrationsHistory"
                    WHERE "MigrationId" = '20261005050000_AddAssetBrandId'
                ) AS "Value"
            """).SingleAsync();
            Assert.True(historyExists);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddAssetBrandId_Downgrade_DbWithBrandData_ThrowsNotSupportedException_AndPreservesBrandData()
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
                VALUES ({{assetId}}, {{ownerId}}, {{eqId}}, {{brandId}}, 'Test Camera', 1, '{"res":"4K"}'::jsonb, true, {{now}}, {{now}});
            """);

            // Attempt downgrade
            var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
                db.Database.MigrateAsync("20261002030000_CompleteInventoryAssetManagement"));
            Assert.Contains("AddAssetBrandId retains brand_id column and constraint", ex.Message);

            // Verify data intact
            var reloadedBrand = await db.Database.SqlQuery<Guid?>($"""
                SELECT brand_id AS "Value" FROM inventory.assets WHERE id = {assetId}
            """).SingleAsync();
            Assert.Equal(brandId, reloadedBrand);

            // Verify constraint intact
            var constraintExists = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.check_constraints
                    WHERE constraint_schema = 'inventory' AND constraint_name = 'ck_assets_brand_id'
                ) AS "Value"
            """).SingleAsync();
            Assert.True(constraintExists);

            // Verify history intact
            var historyExists = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM inventory."__EFMigrationsHistory"
                    WHERE "MigrationId" = '20261005050000_AddAssetBrandId'
                ) AS "Value"
            """).SingleAsync();
            Assert.True(historyExists);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
    [Fact]
    public async Task ModelSnapshot_HasNoPendingModelChanges()
    {
        await using var db = await CreateMigratedDbContextAsync();
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
