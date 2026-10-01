using Dprental.InventoryService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// DbContext trung tâm quản lý toàn bộ 9 bảng thực thể của Inventory Service trong schema inventory.
/// Cấu hình tuân thủ quy tắc ưu tiên Attributes, chỉ sử dụng Fluent API cho Filtered Indexes, Check Constraints,
/// Sequences và các mối quan hệ không thể biểu đạt trọn vẹn qua Data Annotations.
/// </summary>
public sealed class InventoryServiceDbContext : DbContext
{
    /// <summary>
    /// Tên schema dùng chung cho toàn bộ cơ sở dữ liệu của Inventory Service.
    /// </summary>
    public const string SchemaName = "inventory";

    /// <summary>
    /// Tên sequence PostgreSQL cấp phát số lượt đặt lịch (ReservationNumber).
    /// </summary>
    public const string ReservationNumberSequenceName = "ReservationNumberSequence";

    /// <summary>
    /// Bảng quản lý thông tin thiết bị trong kho.
    /// </summary>
    public DbSet<InventoryEquipment> Equipments => Set<InventoryEquipment>();

    /// <summary>
    /// Bảng quản lý các lượt đặt lịch thuê thiết bị.
    /// </summary>
    public DbSet<Reservation> Reservations => Set<Reservation>();

    /// <summary>
    /// Bảng ghi nhận lịch sử và chuyển đổi trạng thái của lượt đặt lịch (append-only).
    /// </summary>
    public DbSet<ReservationTransition> ReservationTransitions => Set<ReservationTransition>();

    /// <summary>
    /// Bảng quản lý các khoảng thời gian chặn lịch sử dụng thiết bị.
    /// </summary>
    public DbSet<CalendarBlock> CalendarBlocks => Set<CalendarBlock>();

    /// <summary>
    /// Bảng ghi nhận báo cáo sự cố hư hỏng từ khách thuê.
    /// </summary>
    public DbSet<DamageReport> DamageReports => Set<DamageReport>();

    /// <summary>
    /// Bảng quản lý các thông báo vận hành gửi tới tổ chức.
    /// </summary>
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>
    /// Bảng lưu trữ thông điệp sự kiện xuất bản ra bên ngoài (Transactional Outbox).
    /// </summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// Bảng kiểm soát tính duy nhất khi tiếp nhận thông điệp (Transactional Inbox).
    /// </summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <summary>
    /// Bảng lưu trữ trạng thái phiên phối hợp phân tán (Saga Pattern).
    /// </summary>
    public DbSet<SagaInstance> SagaInstances => Set<SagaInstance>();

    /// <summary>
    /// Khởi tạo DbContext với DbContextOptions.
    /// </summary>
    public InventoryServiceDbContext(DbContextOptions<InventoryServiceDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// Cấu hình các quy tắc mapping chuyên sâu, index có điều kiện, check constraints và quan hệ không cascade delete.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Thiết lập schema mặc định là "inventory"
        modelBuilder.HasDefaultSchema(SchemaName);

        // Sequence tự tăng cho ReservationNumber do PostgreSQL quản lý, bắt đầu từ 1
        modelBuilder.HasSequence<long>(ReservationNumberSequenceName, SchemaName)
            .StartsAt(1)
            .IncrementsBy(1);

        // 1. Cấu hình thực thể InventoryEquipment
        modelBuilder.Entity<InventoryEquipment>(b =>
        {

            b.HasIndex(e => e.OrganizationId)
                .HasDatabaseName("ix_equipments_organization_id");

            b.HasMany(e => e.Reservations)
                .WithOne(r => r.Equipment)
                .HasForeignKey(r => r.EquipmentId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasMany(e => e.CalendarBlocks)
                .WithOne(cb => cb.Equipment)
                .HasForeignKey(cb => cb.EquipmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // 2. Cấu hình thực thể Reservation
        modelBuilder.Entity<Reservation>(b =>
        {

            // Giá trị mặc định cho ReservationNumber lấy từ sequence
            b.Property(r => r.ReservationNumber)
                .HasDefaultValueSql($"nextval('\"{SchemaName}\".\"{ReservationNumberSequenceName}\"')");

            // Mapping kiểu phức hợp RentalPeriod thành 2 cột start_date và end_date
            b.ComplexProperty(r => r.RentalPeriod, rp =>
            {
                rp.Property(p => p.StartDate)
                    .HasColumnName("start_date")
                    .IsRequired();

                rp.Property(p => p.EndDate)
                    .HasColumnName("end_date")
                    .IsRequired();
            });

            b.Property<List<Guid>>("_deliveryPhotoIds").HasColumnName("delivery_photo_ids").HasColumnType("uuid[]");
            b.Property<List<Guid>>("_returnPhotoIds").HasColumnName("return_photo_ids").HasColumnType("uuid[]");
            b.Navigation(r => r.Transitions).HasField("_transitions").UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Navigation(r => r.DamageReports).HasField("_damageReports").UsePropertyAccessMode(PropertyAccessMode.Field);

            // Unique index cho ReservationNumber
            b.HasIndex(r => r.ReservationNumber)
                .IsUnique()
                .HasDatabaseName("ix_reservations_reservation_number");

            // Unique Filtered Index cho (SourceSystem, ExternalReference) khi ExternalReference khác null
            b.HasIndex(r => new { r.SourceSystem, r.ExternalReference })
                .IsUnique()
                .HasFilter("\"external_reference\" IS NOT NULL")
                .HasDatabaseName("ix_reservations_source_system_external_reference");

            // Index phục vụ quét các hold hết hạn (Held = 1)
            b.HasIndex(r => new { r.Status, r.HoldExpiresAt })
                .HasFilter("\"status\" = 1 AND \"hold_expires_at\" IS NOT NULL")
                .HasDatabaseName("ix_reservations_hold_expiry");

            // Index tìm kiếm theo renter đang thuê
            b.HasIndex(r => new { r.RenterAccountId, r.Status })
                .HasFilter("\"renter_account_id\" IS NOT NULL")
                .HasDatabaseName("ix_reservations_renter_status");

            // Index phục vụ truy vấn lịch và kiểm tra trùng lặp (EquipmentId, Status)
            b.HasIndex(r => new { r.EquipmentId, r.Status })
                .HasDatabaseName("ix_reservations_equipment_status");

            // Không cascade delete sang transitions và damage reports
            b.HasMany(r => r.Transitions)
                .WithOne(t => t.Reservation)
                .HasForeignKey(t => t.ReservationId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasMany(r => r.DamageReports)
                .WithOne(d => d.Reservation)
                .HasForeignKey(d => d.ReservationId)
                .OnDelete(DeleteBehavior.Restrict);

            // Database Check Constraints
            b.ToTable(t =>
            {
                // Thứ tự ngày: start_date <= end_date
                t.HasCheckConstraint("ck_reservations_rental_period_order", "\"start_date\" <= \"end_date\"");

                // Thời lượng thuê từ 1 đến 30 ngày inclusive: (end_date - start_date + 1) BETWEEN 1 AND 30
                t.HasCheckConstraint("ck_reservations_rental_period_duration", "(\"end_date\" - \"start_date\" + 1) >= 1 AND (\"end_date\" - \"start_date\" + 1) <= 30");

                // Bắt buộc có hold_expires_at khi status là Held (1)
                t.HasCheckConstraint("ck_reservations_held_hold_expires_at", "\"status\" <> 1 OR \"hold_expires_at\" IS NOT NULL");

                // Bắt buộc có delivered_at và delivered_by khi status là InUse (3) hoặc Completed (4)
                t.HasCheckConstraint("ck_reservations_delivery_info", "\"status\" NOT IN (3, 4) OR (\"delivered_at\" IS NOT NULL AND \"delivered_by\" IS NOT NULL)");

                // Bắt buộc có returned_at và returned_by khi status là Completed (4)
                t.HasCheckConstraint("ck_reservations_return_info", "\"status\" <> 4 OR (\"returned_at\" IS NOT NULL AND \"returned_by\" IS NOT NULL)");

                t.HasCheckConstraint("ck_reservations_external_metadata", "\"source_type\" <> 2 OR (\"source_system\" IS NOT NULL AND length(btrim(\"source_system\")) > 0 AND \"external_reference\" IS NOT NULL AND length(btrim(\"external_reference\")) > 0 AND \"renter_account_id\" IS NOT NULL)");
                t.HasCheckConstraint("ck_reservations_source_status", "\"source_type\" <> 1 OR \"status\" <> 1");
                t.HasCheckConstraint("ck_reservations_status", "\"status\" BETWEEN 1 AND 6 AND \"source_type\" IN (1, 2)");
                t.HasCheckConstraint("ck_reservations_return_condition", "\"status\" <> 4 OR (\"return_condition\" IS NOT NULL AND \"return_condition\" IN (1, 2))");
                t.HasCheckConstraint("ck_reservations_return_time", "\"returned_at\" IS NULL OR \"returned_at\" > \"delivered_at\"");
                t.HasCheckConstraint("ck_reservations_damaged_evidence", "\"return_condition\" IS DISTINCT FROM 2 OR (\"return_condition_notes\" IS NOT NULL AND length(btrim(\"return_condition_notes\")) > 0 AND cardinality(\"return_photo_ids\") BETWEEN 1 AND 5)");

                // Tối đa 5 ảnh bàn giao
                t.HasCheckConstraint("ck_reservations_delivery_photos_count", "cardinality(\"delivery_photo_ids\") <= 5");

                // Tối đa 5 ảnh thu hồi
                t.HasCheckConstraint("ck_reservations_return_photos_count", "cardinality(\"return_photo_ids\") <= 5");
            });
        });

        // 3. Cấu hình thực thể ReservationTransition
        modelBuilder.Entity<ReservationTransition>(b =>
        {
            // Unique index cho Sequence trong cùng một reservation
            b.HasIndex(t => new { t.ReservationId, t.Sequence })
                .IsUnique()
                .HasDatabaseName("ix_reservation_transitions_reservation_sequence");

            // Unique Filtered Index cho OperationKey trong cùng một reservation
            b.HasIndex(t => new { t.ReservationId, t.OperationKey })
                .IsUnique()
                .HasFilter("\"operation_key\" IS NOT NULL")
                .HasDatabaseName("ix_reservation_transitions_reservation_operation_key");

            // Index timeline hỗ trợ truy vấn dòng thời gian nhanh
            b.HasIndex(t => new { t.ReservationId, t.CreatedAt })
                .HasDatabaseName("ix_reservation_transitions_timeline");
        });

        // 4. Cấu hình thực thể CalendarBlock
        modelBuilder.Entity<CalendarBlock>(b =>
        {

            b.HasIndex(cb => new { cb.EquipmentId, cb.StartDate, cb.EndDate })
                .HasDatabaseName("ix_calendar_blocks_equipment_dates");

            b.ToTable(t =>
            {
                // Thứ tự ngày chặn lịch: start_date <= end_date
                t.HasCheckConstraint("ck_calendar_blocks_date_order", "\"start_date\" <= \"end_date\"");
            });
        });

        // 5. Cấu hình thực thể DamageReport
        modelBuilder.Entity<DamageReport>(b =>
        {
            b.Property<List<Guid>>("_photoMediaIds").HasColumnName("photo_media_ids").HasColumnType("uuid[]");

            b.HasIndex(d => d.ReservationId)
                .HasDatabaseName("ix_damage_reports_reservation_id");

            b.HasIndex(d => d.ReporterAccountId)
                .HasDatabaseName("ix_damage_reports_reporter_account_id");

            b.ToTable(t =>
            {
                t.HasCheckConstraint("ck_damage_reports_description", "length(btrim(\"description\")) BETWEEN 10 AND 1000");
                // Tối đa 5 ảnh minh chứng hư hỏng
                t.HasCheckConstraint("ck_damage_reports_photos_count", "cardinality(\"photo_media_ids\") <= 5");
            });
        });

        // 6. Cấu hình thực thể Notification
        modelBuilder.Entity<Notification>(b =>
        {

            // Unique deduplication key theo tổ chức
            b.HasIndex(n => new { n.OrganizationId, n.DeduplicationKey })
                .IsUnique()
                .HasDatabaseName("ix_notifications_organization_dedup");

            // Index truy vấn thông báo chưa xem hoặc đã hết hạn
            b.HasIndex(n => new { n.OrganizationId, n.ViewedAt, n.ExpiresAt })
                .HasDatabaseName("ix_notifications_organization_viewed_expires");

            b.HasOne(n => n.Reservation)
                .WithMany()
                .HasForeignKey(n => n.ReservationId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasOne(n => n.DamageReport)
                .WithMany()
                .HasForeignKey(n => n.DamageReportId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // 7. Cấu hình thực thể OutboxMessage
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            // Index phục vụ background worker quét các thông điệp pending / retry
            b.HasIndex(o => new { o.ProcessedAt, o.RetryCount, o.LockLeaseExpiresAt })
                .HasDatabaseName("ix_outbox_messages_pending_retry");
        });

        // 8. Cấu hình thực thể InboxMessage
        modelBuilder.Entity<InboxMessage>(b =>
        {
            // Unique index chống trùng lặp theo Consumer và MessageId
            b.HasIndex(i => new { i.Consumer, i.MessageId })
                .IsUnique()
                .HasDatabaseName("ix_inbox_messages_consumer_message");

            // Index quét thông điệp theo tiến độ và hạn lease
            b.HasIndex(i => new { i.ProcessedAt, i.LockLeaseExpiresAt })
                .HasDatabaseName("ix_inbox_messages_processed_lease");
        });

        // 9. Cấu hình thực thể SagaInstance
        modelBuilder.Entity<SagaInstance>(b =>
        {

            // Unique index theo SagaType và CorrelationId
            b.HasIndex(s => new { s.SagaType, s.CorrelationId })
                .IsUnique()
                .HasDatabaseName("ix_saga_instances_type_correlation");

            // Index tìm kiếm phiên saga cần xử lý bước tiếp theo
            b.HasIndex(s => new { s.NextProcessAt, s.State })
                .HasDatabaseName("ix_saga_instances_next_process_state");
        });
    }
}
