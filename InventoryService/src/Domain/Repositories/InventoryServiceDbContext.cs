using Dprental.InventoryService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// DbContext trung tâm quản lý 6 bảng thực thể của Inventory Service trong schema inventory:
/// assets, asset_allocations, allocation_transitions, calendar_blocks, outbox_messages, inbox_messages.
/// </summary>
public sealed class InventoryServiceDbContext : DbContext
{
    /// <summary>
    /// Tên schema dùng chung cho toàn bộ cơ sở dữ liệu của Inventory Service.
    /// </summary>
    public const string SchemaName = "inventory";

    /// <summary>Migration history thuộc schema Inventory, độc lập với các service khác.</summary>
    public const string MigrationsHistoryTableName = "__EFMigrationsHistory";

    /// <summary>
    /// Bảng quản lý thông tin tài sản trong kho.
    /// </summary>
    public DbSet<InventoryAsset> Assets => Set<InventoryAsset>();

    /// <summary>
    /// Bảng quản lý danh mục niêm yết thiết bị.
    /// </summary>
    public DbSet<EquipmentListing> EquipmentListings => Set<EquipmentListing>();
    /// <summary>
    /// Bảng quản lý kho lưu trữ của các bên cho thuê thiết bị.
    /// </summary>
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();


    /// <summary>
    /// Bảng quản lý các lượt phân bổ lịch tài sản.
    /// </summary>
    public DbSet<AssetAllocation> AssetAllocations => Set<AssetAllocation>();

    /// <summary>
    /// Bảng ghi nhận lịch sử và chuyển đổi trạng thái của lượt phân bổ (append-only).
    /// </summary>
    public DbSet<AllocationTransition> AllocationTransitions => Set<AllocationTransition>();

    /// <summary>
    /// Bảng lưu trữ biên nhận tác vụ phân bổ phục vụ idempotent replay và serialization.
    /// </summary>
    public DbSet<AllocationOperationReceipt> AllocationOperationReceipts => Set<AllocationOperationReceipt>();

    /// <summary>
    /// Bảng quản lý các khoảng thời gian chặn lịch sử dụng tài sản.
    /// </summary>
    public DbSet<CalendarBlock> CalendarBlocks => Set<CalendarBlock>();

    /// <summary>
    /// Bảng lưu trữ thông điệp sự kiện xuất bản ra bên ngoài (Transactional Outbox).
    /// </summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// Bảng kiểm soát tính duy nhất khi tiếp nhận thông điệp (Transactional Inbox).
    /// </summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    /// <summary>
    /// Bảng quản lý hàng đợi công việc đồng bộ chỉ mục tìm kiếm (Search Projection Jobs).
    /// </summary>
    public DbSet<SearchProjectionJob> SearchProjectionJobs => Set<SearchProjectionJob>();
    /// <summary>
    /// Bảng quản lý generation và readiness của chỉ mục tìm kiếm live.
    /// </summary>
    public DbSet<SearchBackfillReceipt> SearchBackfillReceipts => Set<SearchBackfillReceipt>();

    public DbSet<SearchGeneration> SearchGenerations => Set<SearchGeneration>();

    /// <summary>
    /// Bảng quản lý các đợt chạy backfill và staging swap.
    /// </summary>
    public DbSet<SearchBackfillRun> SearchBackfillRuns => Set<SearchBackfillRun>();


    /// <summary>
    /// Bảng quản lý hàng đợi công việc xóa cache (Cache Invalidation Jobs).
    /// </summary>
    public DbSet<CacheInvalidationJob> CacheInvalidationJobs => Set<CacheInvalidationJob>();
    public InventoryServiceDbContext(DbContextOptions<InventoryServiceDbContext> options) : base(options)
    {
    }


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>("search_projection_revision_seq", SchemaName)
            .StartsAt(1)
            .IncrementsBy(1);
        base.OnModelCreating(modelBuilder);

        // Thiết lập schema mặc định là "inventory"
        modelBuilder.HasDefaultSchema(SchemaName);

        // 1. Cấu hình thực thể EquipmentListing
        modelBuilder.Entity<EquipmentListing>(b =>
        {
            b.ToTable("equipment_listings", SchemaName, t =>
            {
                t.HasCheckConstraint("ck_equipment_listings_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND owner_user_id <> '00000000-0000-0000-0000-000000000000'::uuid AND equipment_type_id <> '00000000-0000-0000-0000-000000000000'::uuid AND brand_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_equipment_listings_name", "name ~ '\\S' AND length(name) <= 200");
                t.HasCheckConstraint("ck_equipment_listings_description", "description IS NULL OR (description ~ '\\S' AND length(description) <= 2000)");
                t.HasCheckConstraint("ck_equipment_listings_model", "model ~ '\\S' AND length(model) <= 200");
                t.HasCheckConstraint("ck_equipment_listings_manufacture_year", "manufacture_year IS NULL OR manufacture_year >= 1900");
                t.HasCheckConstraint("ck_equipment_listings_media_ids", "cardinality(media_ids) <= 20 AND array_position(media_ids, NULL) IS NULL AND NOT ('00000000-0000-0000-0000-000000000000'::uuid = ANY(media_ids)) AND (thumbnail_media_id IS NULL OR thumbnail_media_id = ANY(media_ids))");
                t.HasCheckConstraint("ck_equipment_listings_thumbnail_media_id", "thumbnail_media_id IS NULL OR thumbnail_media_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_equipment_listings_schema_version", "schema_version >= 1");
                t.HasCheckConstraint("ck_equipment_listings_spec_values_object", "jsonb_typeof(spec_values) = 'object'");
                t.HasCheckConstraint("ck_equipment_listings_daily_rate", "daily_rate >= 1 AND daily_rate <= 999999999999999999 AND trunc(daily_rate) = daily_rate");
                t.HasCheckConstraint("ck_equipment_listings_weekly_rate", "weekly_rate IS NULL OR (weekly_rate >= 1 AND weekly_rate <= 999999999999999999 AND trunc(weekly_rate) = weekly_rate)");
                t.HasCheckConstraint("ck_equipment_listings_monthly_rate", "monthly_rate IS NULL OR (monthly_rate >= 1 AND monthly_rate <= 999999999999999999 AND trunc(monthly_rate) = monthly_rate)");
                t.HasCheckConstraint("ck_equipment_listings_deposit_amount", "deposit_amount >= 0 AND deposit_amount <= 999999999999999999 AND trunc(deposit_amount) = deposit_amount");
                t.HasCheckConstraint("ck_equipment_listings_currency", "currency = 'VND'");
                t.HasCheckConstraint("ck_equipment_listings_status", "\"status\" IN (1, 2, 3)");
            });

            b.HasIndex(l => l.OwnerUserId)
                .HasDatabaseName("ix_equipment_listings_owner_user_id");
            b.HasIndex(l => l.EquipmentTypeId)
                .HasDatabaseName("ix_equipment_listings_equipment_type_id");
            b.HasIndex(l => l.BrandId)
                .HasDatabaseName("ix_equipment_listings_brand_id");
            b.HasIndex(l => l.Status)
                .HasDatabaseName("ix_equipment_listings_status");
            b.HasIndex(l => l.DailyRate)
                .HasDatabaseName("ix_equipment_listings_daily_rate");

            b.Property(l => l.Name)
                .HasColumnName("name")
                .HasMaxLength(200)
                .IsRequired();

            b.Property(l => l.Description)
                .HasColumnName("description")
                .HasMaxLength(2000);

            b.Property(l => l.Model)
                .HasColumnName("model")
                .HasMaxLength(200)
                .IsRequired();


            b.Property(l => l.ManufactureYear)
                .HasColumnName("manufacture_year");

            b.Property(l => l.MediaIds)
                .HasColumnName("media_ids")
                .HasColumnType("uuid[]")
                .HasDefaultValueSql("'{}'::uuid[]")
                .IsRequired();
            b.Property(l => l.SpecValues)
                .HasColumnType("jsonb")
                .IsRequired();

            b.Property(l => l.DailyRate)
                .HasColumnName("daily_rate")
                .HasColumnType("numeric(18,0)")
                .IsRequired();

            b.Property(l => l.WeeklyRate)
                .HasColumnName("weekly_rate")
                .HasColumnType("numeric(18,0)");

            b.Property(l => l.MonthlyRate)
                .HasColumnName("monthly_rate")
                .HasColumnType("numeric(18,0)");

            b.Property(l => l.DepositAmount)
                .HasColumnName("deposit_amount")
                .HasColumnType("numeric(18,0)")
                .HasDefaultValue(0m)
                .IsRequired();

            b.Property(l => l.Currency)
                .HasColumnName("currency")
                .HasMaxLength(10)
                .IsRequired();

            b.HasMany(l => l.Assets)
                .WithOne(a => a.Listing)
                .HasForeignKey(a => a.EquipmentListingId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne(l => l.Warehouse)
                .WithMany(w => w.Listings)
                .HasForeignKey(l => l.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // 2. Cấu hình thực thể InventoryAsset
        // Cấu hình thực thể Warehouse
        modelBuilder.Entity<Warehouse>(b =>
        {
            b.ToTable("warehouses", SchemaName, t =>
            {
                t.HasCheckConstraint("ck_warehouses_identity", "owner_user_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_warehouses_latitude", "latitude IS NULL OR (latitude >= -90.0 AND latitude <= 90.0)");
                t.HasCheckConstraint("ck_warehouses_longitude", "longitude IS NULL OR (longitude >= -180.0 AND longitude <= 180.0)");
                t.HasCheckConstraint("ck_warehouses_address_detail", "address_detail IS NULL OR (address_detail ~ '\\S' AND length(address_detail) <= 500)");
                t.HasCheckConstraint("ck_warehouses_province_code", "province_code IS NULL OR (province_code ~ '\\S' AND length(province_code) <= 20)");
                t.HasCheckConstraint("ck_warehouses_province_name", "province_name IS NULL OR (province_name ~ '\\S' AND length(province_name) <= 200)");
                t.HasCheckConstraint("ck_warehouses_ward_code", "ward_code IS NULL OR (ward_code ~ '\\S' AND length(ward_code) <= 20)");
                t.HasCheckConstraint("ck_warehouses_ward_name", "ward_name IS NULL OR (ward_name ~ '\\S' AND length(ward_name) <= 200)");
            });

            b.HasKey(w => w.OwnerUserId);

            b.HasIndex(w => new { w.ProvinceCode, w.WardCode })
                .HasDatabaseName("ix_warehouses_province_ward");

            b.Property(w => w.OwnerUserId)
                .HasColumnName("owner_user_id")
                .IsRequired();

            b.Property(w => w.AddressDetail)
                .HasColumnName("address_detail")
                .HasMaxLength(500);

            b.Property(w => w.ProvinceCode)
                .HasColumnName("province_code")
                .HasMaxLength(20);

            b.Property(w => w.ProvinceName)
                .HasColumnName("province_name")
                .HasMaxLength(200);

            b.Property(w => w.WardCode)
                .HasColumnName("ward_code")
                .HasMaxLength(20);

            b.Property(w => w.WardName)
                .HasColumnName("ward_name")
                .HasMaxLength(200);

            b.Property(w => w.Latitude)
                .HasColumnName("latitude");

            b.Property(w => w.Longitude)
                .HasColumnName("longitude");

            b.Property(w => w.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired();

            b.Property(w => w.UpdatedAt)
                .HasColumnName("updated_at")
                .IsRequired();
        });

        modelBuilder.Entity<InventoryAsset>(b =>
        {
            b.ToTable("assets", SchemaName, t =>
            {
                t.HasCheckConstraint("ck_assets_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND equipment_listing_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_assets_asset_code", "asset_code ~ '^AST-[0-9a-fA-F]{32}$'");
                t.HasCheckConstraint("ck_assets_serial_number", "length(btrim(serial_number)) > 0 AND length(serial_number) <= 100");
                t.HasCheckConstraint("ck_assets_manufacture_year", "manufacture_year IS NULL OR manufacture_year >= 1900");
            });

            b.HasIndex(a => a.EquipmentListingId)
                .HasDatabaseName("ix_assets_equipment_listing_id");

            b.HasIndex(a => a.AssetCode)
                .IsUnique()
                .HasDatabaseName("ix_assets_asset_code");

            b.Property(a => a.AssetCode)
                .HasMaxLength(50)
                .IsRequired();
            b.Property(a => a.SerialNumber)
                .HasColumnName("serial_number")
                .HasMaxLength(100)
                .IsRequired();
            b.HasMany(a => a.Allocations)
                .WithOne(al => al.Asset)
                .HasForeignKey(al => al.AssetId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasMany(a => a.CalendarBlocks)
                .WithOne(cb => cb.Asset)
                .HasForeignKey(cb => cb.AssetId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // 2. Cấu hình thực thể AssetAllocation
        modelBuilder.Entity<AssetAllocation>(b =>
        {
            b.ComplexProperty(a => a.RentalPeriod, rp =>
            {
                rp.Property(p => p.StartDate)
                    .HasColumnName("start_date")
                    .IsRequired();

                rp.Property(p => p.EndDate)
                    .HasColumnName("end_date")
                    .IsRequired();
            });

            b.Navigation(a => a.Transitions)
                .HasField("_transitions")
                .UsePropertyAccessMode(PropertyAccessMode.Field);

            b.HasIndex(a => new { a.SourceSystem, a.ExternalReference })
                .IsUnique()
                .HasDatabaseName("ix_asset_allocations_source_system_external_reference");

            b.HasIndex(a => new { a.Status, a.HoldExpiresAt })
                .HasFilter("\"status\" = 1 AND \"hold_expires_at\" IS NOT NULL")
                .HasDatabaseName("ix_asset_allocations_hold_expiry");

            b.HasIndex(a => new { a.AssetId, a.Status })
                .HasDatabaseName("ix_asset_allocations_asset_status");

            b.Property(a => a.GroupReference)
                .HasMaxLength(100);

            b.HasIndex(a => new { a.SourceSystem, a.GroupReference })
                .HasFilter("\"group_reference\" IS NOT NULL")
                .HasDatabaseName("ix_asset_allocations_source_system_group_reference");

            b.HasIndex(a => new { a.SourceSystem, a.GroupReference, a.AssetId })
                .IsUnique()
                .HasFilter("\"group_reference\" IS NOT NULL")
                .HasDatabaseName("ix_asset_allocations_source_system_group_asset");

            b.HasMany(a => a.Transitions)
                .WithOne(t => t.Allocation)
                .HasForeignKey(t => t.AllocationId)
                .OnDelete(DeleteBehavior.Restrict);

            b.ToTable(t =>
            {
                t.HasCheckConstraint("ck_asset_allocations_rental_period_order", "\"start_date\" <= \"end_date\"");
                t.HasCheckConstraint("ck_asset_allocations_held_hold_expires_at", "\"status\" <> 1 OR \"hold_expires_at\" IS NOT NULL");
                t.HasCheckConstraint("ck_asset_allocations_status", "\"status\" IN (1, 2, 5, 6)");
                t.HasCheckConstraint("ck_asset_allocations_source_reference", "length(btrim(\"source_system\")) > 0 AND length(btrim(\"external_reference\")) > 0");
                t.HasCheckConstraint("ck_asset_allocations_group_reference", "\"group_reference\" IS NULL OR (length(btrim(\"group_reference\")) > 0 AND length(\"group_reference\") <= 100)");
                t.HasCheckConstraint("ck_asset_allocations_handover_return", "(\"returned_at\" IS NULL OR \"handed_over_at\" IS NOT NULL) AND (\"returned_at\" IS NULL OR \"returned_at\" >= \"handed_over_at\")");
            });
        });

        // 3. Cấu hình thực thể AllocationTransition
        modelBuilder.Entity<AllocationTransition>(b =>
        {
            b.HasIndex(t => new { t.AllocationId, t.Sequence })
                .IsUnique()
                .HasDatabaseName("ix_allocation_transitions_allocation_sequence");

            b.HasIndex(t => new { t.AllocationId, t.OperationKey })
                .IsUnique()
                .HasFilter("\"operation_key\" IS NOT NULL")
                .HasDatabaseName("ix_allocation_transitions_allocation_operation_key");

            b.HasIndex(t => new { t.AllocationId, t.CreatedAt })
                .HasDatabaseName("ix_allocation_transitions_timeline");
        });

        // Cấu hình thực thể AllocationOperationReceipt
        modelBuilder.Entity<AllocationOperationReceipt>(b =>
        {
            b.HasIndex(r => new { r.SourceSystem, r.OperationKey })
                .IsUnique()
                .HasDatabaseName("ix_allocation_operation_receipts_source_opkey");

            b.HasIndex(r => new { r.SourceSystem, r.GroupReference })
                .HasFilter("\"group_reference\" IS NOT NULL")
                .HasDatabaseName("ix_allocation_operation_receipts_group");

            b.HasIndex(r => r.RentalOrderId)
                .HasDatabaseName("ix_allocation_operation_receipts_order");
        });

        // 4. Cấu hình thực thể CalendarBlock
        modelBuilder.Entity<CalendarBlock>(b =>
        {
            b.HasIndex(cb => new { cb.AssetId, cb.StartDate, cb.EndDate })
                .HasDatabaseName("ix_calendar_blocks_asset_dates");

            b.ToTable(t =>
            {
                t.HasCheckConstraint("ck_calendar_blocks_date_order", "\"start_date\" <= \"end_date\"");
            });
        });

        // 5. Cấu hình thực thể OutboxMessage
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.HasIndex(o => new { o.PublishedAt, o.NextAttemptAt, o.LockLeaseExpiresAt, o.RetryCount, o.CreatedAt })
                .HasDatabaseName("ix_outbox_messages_pending_claim");

            b.HasIndex(o => new { o.KafkaKey, o.PublishedAt, o.CreatedAt })
                .HasDatabaseName("ix_outbox_messages_key_ordering");
        });

        // 6. Cấu hình thực thể InboxMessage
        modelBuilder.Entity<InboxMessage>(b =>
        {
            b.HasIndex(i => new { i.Handler, i.MessageId })
                .IsUnique()
                .HasDatabaseName("ix_inbox_messages_handler_message");

            b.HasIndex(i => new { i.ProcessedAt, i.LockLeaseExpiresAt })
                .HasDatabaseName("ix_inbox_messages_processed_lease");

            b.HasIndex(i => new { i.Status, i.NextAttemptAt })
                .HasDatabaseName("ix_inbox_messages_status_next_attempt");
        });

        // 7. Cấu hình thực thể SearchProjectionJob
        modelBuilder.Entity<SearchProjectionJob>(b =>
        {
            b.ToTable("search_projection_jobs", SchemaName);
            b.HasKey(j => j.Id);
            b.HasIndex(j => new { j.EntityType, j.EntityId })
                .IsUnique()
                .HasDatabaseName("ix_search_projection_jobs_entity");
            b.HasIndex(j => new { j.Status, j.NextAttemptAt, j.LockLeaseExpiresAt })
                .HasDatabaseName("ix_search_projection_jobs_claim");
        });

        modelBuilder.Entity<SearchBackfillReceipt>().HasIndex(r => new { r.RunId, r.MessageId }).IsUnique();
        modelBuilder.Entity<SearchBackfillRun>().Property<int>("ActiveSlot").HasColumnName("active_slot").HasComputedColumnSql("1", stored: true);
        modelBuilder.Entity<SearchBackfillRun>().HasIndex("ActiveSlot")
            .HasDatabaseName("ux_search_backfill_active").IsUnique()
            .HasFilter("status IN ('Building', 'CatchingUp', 'Swapping')");

        // 8. Cấu hình thực thể SearchGeneration
        modelBuilder.Entity<SearchGeneration>(b =>
        {
            b.ToTable("search_generations", SchemaName);
            b.HasKey(g => g.Id);
        });

        // 9. Cấu hình thực thể SearchBackfillRun
        modelBuilder.Entity<SearchBackfillRun>(b =>
        {
            b.ToTable("search_backfill_runs", SchemaName);
            b.HasKey(r => r.RunId);
            b.HasIndex(r => r.Status).HasDatabaseName("ix_search_backfill_runs_status");
        });


        // 10. Cấu hình thực thể CacheInvalidationJob
        modelBuilder.Entity<CacheInvalidationJob>(b =>
        {
            b.ToTable("cache_invalidation_jobs", SchemaName);
            b.HasKey(j => j.Id);
            b.Property(j => j.Scopes)
                .HasColumnName("scopes")
                .HasColumnType("text[]");
            b.Property(j => j.FanoutOwnerUserId)
                .HasColumnName("fanout_owner_user_id");
            b.Property(j => j.FanoutAfterListingId)
                .HasColumnName("fanout_after_listing_id");
            b.HasIndex(j => new { j.Status, j.NextAttemptAt, j.LockLeaseExpiresAt })
                .HasDatabaseName("ix_cache_invalidation_jobs_claim");
        });
    }
}
