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
    /// Bảng quản lý các lượt phân bổ lịch tài sản.
    /// </summary>
    public DbSet<AssetAllocation> AssetAllocations => Set<AssetAllocation>();

    /// <summary>
    /// Bảng ghi nhận lịch sử và chuyển đổi trạng thái của lượt phân bổ (append-only).
    /// </summary>
    public DbSet<AllocationTransition> AllocationTransitions => Set<AllocationTransition>();

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

    public InventoryServiceDbContext(DbContextOptions<InventoryServiceDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Thiết lập schema mặc định là "inventory"
        modelBuilder.HasDefaultSchema(SchemaName);

        // 1. Cấu hình thực thể EquipmentListing
        modelBuilder.Entity<EquipmentListing>(b =>
        {
            b.ToTable("equipment_listings", SchemaName, t =>
            {
                t.HasCheckConstraint("ck_equipment_listings_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND owner_user_id <> '00000000-0000-0000-0000-000000000000'::uuid AND equipment_type_id <> '00000000-0000-0000-0000-000000000000'::uuid AND brand_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_equipment_listings_model", "model ~ '\\S' AND length(model) <= 200");
                t.HasCheckConstraint("ck_equipment_listings_thumbnail_media_id", "thumbnail_media_id IS NULL OR thumbnail_media_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_equipment_listings_schema_version", "schema_version >= 1");
                t.HasCheckConstraint("ck_equipment_listings_spec_values_object", "jsonb_typeof(spec_values) = 'object'");
                t.HasCheckConstraint("ck_equipment_listings_daily_rate", "daily_rate >= 1 AND daily_rate <= 999999999999999999 AND trunc(daily_rate) = daily_rate");
                t.HasCheckConstraint("ck_equipment_listings_weekly_rate", "weekly_rate IS NULL OR (weekly_rate >= 1 AND weekly_rate <= 999999999999999999 AND trunc(weekly_rate) = weekly_rate)");
                t.HasCheckConstraint("ck_equipment_listings_monthly_rate", "monthly_rate IS NULL OR (monthly_rate >= 1 AND monthly_rate <= 999999999999999999 AND trunc(monthly_rate) = monthly_rate)");
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

            b.Property(l => l.Model)
                .HasMaxLength(200)
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

            b.Property(l => l.Currency)
                .HasColumnName("currency")
                .HasMaxLength(10)
                .IsRequired();

            b.HasMany(l => l.Assets)
                .WithOne(a => a.Listing)
                .HasForeignKey(a => a.EquipmentListingId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // 2. Cấu hình thực thể InventoryAsset
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

    }
}
