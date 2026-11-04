using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Dprental.ReviewService.Domain.Repositories;

/// <summary>
/// DbContext quản lý dữ liệu ReviewService trên PostgreSQL.
/// </summary>
public sealed class ReviewServiceDbContext : DbContext
{
    public ReviewServiceDbContext(DbContextOptions<ReviewServiceDbContext> options) : base(options)
    {
    }

    /// <summary>Bảng lưu trữ đánh giá người dùng.</summary>
    public DbSet<Review> Reviews => Set<Review>();

    /// <summary>Bảng lưu trữ nhật ký kiểm toán đánh giá.</summary>
    public DbSet<ReviewAudit> ReviewAudits => Set<ReviewAudit>();

    /// <summary>Bảng lưu trữ bản ghi chống trùng lặp yêu cầu API.</summary>
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    /// <summary>Bảng lưu trữ báo cáo vi phạm đánh giá.</summary>
    public DbSet<ReviewReport> ReviewReports => Set<ReviewReport>();

    /// <summary>Bảng lưu trữ projection điều kiện đánh giá.</summary>
    public DbSet<ReviewProjection> ReviewProjections => Set<ReviewProjection>();

    /// <summary>Bảng transactional inbox deduplication sự kiện Kafka.</summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <summary>Bảng lưu checkpoint đối soát sweep 30 ngày.</summary>
    public DbSet<ReconciliationCheckpoint> ReconciliationCheckpoints => Set<ReconciliationCheckpoint>();

    /// <summary>Bảng transactional outbox phát hành sự kiện ReviewService.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>Bảng lưu trữ media đính kèm đánh giá.</summary>
    public DbSet<ReviewMedia> ReviewMedia => Set<ReviewMedia>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureReviews(modelBuilder);
        ConfigureReviewAudits(modelBuilder);
        ConfigureIdempotencyRecords(modelBuilder);
        ConfigureReviewReports(modelBuilder);
        ConfigureReviewProjections(modelBuilder);
        ConfigureInboxMessages(modelBuilder);
        ConfigureReconciliationCheckpoints(modelBuilder);
        ConfigureOutboxMessages(modelBuilder);
        ConfigureReviewMedia(modelBuilder);
    }

    private static void ConfigureReviews(ModelBuilder modelBuilder)
    {
        var directionConverter = new ValueConverter<ReviewDirection, string>(
            v => v == ReviewDirection.supplier_to_renter ? "supplier_to_renter" : "renter_to_supplier_listing",
            v => v == "supplier_to_renter" || v == nameof(ReviewDirection.SupplierToRenter)
                ? ReviewDirection.supplier_to_renter
                : ReviewDirection.renter_to_supplier_listing);

        var authorStateConverter = new ValueConverter<AuthorState, string>(
            v => v.ToString(),
            v => Enum.Parse<AuthorState>(v));

        var moderationStateConverter = new ValueConverter<ModerationState, string>(
            v => v.ToString(),
            v => Enum.Parse<ModerationState>(v));

        modelBuilder.Entity<Review>(entity =>
        {
            entity.ToTable("reviews", t =>
            {
                t.HasCheckConstraint("chk_reviews_direction", "direction IN ('renter_to_supplier_listing', 'supplier_to_renter')");
                t.HasCheckConstraint("chk_reviews_rating", "rating >= 1 AND rating <= 5");
                t.HasCheckConstraint("chk_reviews_author_state", "author_state IN ('Active', 'Withdrawn')");
                t.HasCheckConstraint("chk_reviews_moderation_state", "moderation_state IN ('Visible', 'Hidden')");
            });

            entity.HasKey(r => r.Id);

            entity.Property(r => r.Id)
                .HasColumnName("id");

            entity.Property(r => r.OrderId)
                .HasColumnName("order_id")
                .IsRequired();

            entity.Property(r => r.ItemId)
                .HasColumnName("item_id")
                .IsRequired();

            entity.Property(r => r.Direction)
                .HasColumnName("direction")
                .HasMaxLength(32)
                .HasConversion(directionConverter)
                .IsRequired();

            entity.Property(r => r.AuthorId)
                .HasColumnName("author_id")
                .IsRequired();

            entity.Property(r => r.SubjectId)
                .HasColumnName("subject_id")
                .IsRequired();

            entity.Property(r => r.EquipmentListingId)
                .HasColumnName("equipment_listing_id")
                .IsRequired();

            entity.Property(r => r.Rating)
                .HasColumnName("rating")
                .IsRequired();

            entity.Property(r => r.Comment)
                .HasColumnName("comment");

            entity.Property(r => r.AuthorState)
                .HasColumnName("author_state")
                .HasMaxLength(32)
                .HasConversion(authorStateConverter)
                .HasDefaultValue(AuthorState.Active)
                .IsRequired();

            entity.Property(r => r.ModerationState)
                .HasColumnName("moderation_state")
                .HasMaxLength(32)
                .HasConversion(moderationStateConverter)
                .HasDefaultValue(ModerationState.Visible)
                .IsRequired();

            entity.Property(r => r.Version)
                .HasColumnName("version")
                .HasDefaultValue(1L)
                .IsConcurrencyToken()
                .IsRequired();

            entity.Property(r => r.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired();

            entity.Property(r => r.UpdatedAt)
                .HasColumnName("updated_at");

            entity.Property(r => r.WithdrawnAt)
                .HasColumnName("withdrawn_at");

            entity.Property(r => r.AdminHiddenAt)
                .HasColumnName("admin_hidden_at");

            entity.Property(r => r.AdminReason)
                .HasColumnName("admin_reason");

            entity.Property(r => r.CanEditUntil)
                .HasColumnName("can_edit_until")
                .IsRequired();

            entity.HasIndex(r => new { r.ItemId, r.Direction })
                .IsUnique()
                .HasDatabaseName("uq_reviews_item_direction");

            entity.HasIndex(r => new { r.EquipmentListingId, r.CreatedAt })
                .HasDatabaseName("idx_reviews_listing_active_visible")
                .HasFilter("direction = 'renter_to_supplier_listing' AND author_state = 'Active' AND moderation_state = 'Visible'");

            entity.HasIndex(r => new { r.SubjectId, r.CreatedAt })
                .HasDatabaseName("idx_reviews_supplier_active_visible")
                .HasFilter("direction = 'renter_to_supplier_listing' AND author_state = 'Active' AND moderation_state = 'Visible'");

            entity.HasIndex(r => new { r.AuthorId, r.CreatedAt })
                .HasDatabaseName("idx_reviews_author");

            entity.HasIndex(r => new { r.SubjectId, r.CreatedAt })
                .HasDatabaseName("idx_reviews_subject_private")
                .HasFilter("direction = 'supplier_to_renter'");
        });
    }

    private static void ConfigureReviewAudits(ModelBuilder modelBuilder)
    {
        var actionConverter = new ValueConverter<ReviewAuditAction, string>(
            v => v.ToString(),
            v => Enum.Parse<ReviewAuditAction>(v));

        modelBuilder.Entity<ReviewAudit>(entity =>
        {
            entity.ToTable("review_audits", t =>
            {
                t.HasCheckConstraint("chk_audits_action",
                    "action IN ('Created', 'Updated', 'Withdrawn', 'Hidden', 'Restored', 'Reported', 'AdminHidden', 'AdminRestored')");
            });

            entity.HasKey(a => a.Id);

            entity.Property(a => a.Id)
                .HasColumnName("id");

            entity.Property(a => a.ReviewId)
                .HasColumnName("review_id")
                .IsRequired();

            entity.Property(a => a.ActorId)
                .HasColumnName("actor_id")
                .IsRequired();

            entity.Property(a => a.ActorRole)
                .HasColumnName("actor_role")
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(a => a.Action)
                .HasColumnName("action")
                .HasMaxLength(32)
                .HasConversion(actionConverter)
                .IsRequired();

            entity.Property(a => a.Reason)
                .HasColumnName("reason");

            entity.Property(a => a.OldValues)
                .HasColumnName("old_values")
                .HasColumnType("jsonb");

            entity.Property(a => a.NewValues)
                .HasColumnName("new_values")
                .HasColumnType("jsonb");

            entity.Property(a => a.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired();

            entity.HasOne(a => a.Review)
                .WithMany(r => r.Audits)
                .HasForeignKey(a => a.ReviewId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_audits_review");

            entity.HasIndex(a => new { a.ReviewId, a.CreatedAt })
                .HasDatabaseName("idx_audits_review_id");

            entity.HasIndex(a => new { a.ActorId, a.CreatedAt })
                .HasDatabaseName("idx_audits_actor_id");
        });
    }

    private static void ConfigureIdempotencyRecords(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.ToTable("idempotency_records");

            entity.HasKey(r => r.Id);

            entity.Property(r => r.Id)
                .HasColumnName("id");

            entity.Property(r => r.ActorId)
                .HasColumnName("actor_id")
                .IsRequired();

            entity.Property(r => r.IdempotencyKey)
                .HasColumnName("idempotency_key")
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(r => r.RequestFingerprint)
                .HasColumnName("request_fingerprint")
                .HasMaxLength(64)
                .IsRequired();

            entity.Property(r => r.HttpMethod)
                .HasColumnName("http_method")
                .HasMaxLength(16)
                .IsRequired();

            entity.Property(r => r.RequestPath)
                .HasColumnName("request_path")
                .HasMaxLength(256)
                .IsRequired();

            entity.Property(r => r.ResponseStatusCode)
                .HasColumnName("response_status_code")
                .IsRequired();

            entity.Property(r => r.ResponseHeaders)
                .HasColumnName("response_headers");

            entity.Property(r => r.ResponseBody)
                .HasColumnName("response_body")
                .IsRequired();

            entity.Property(r => r.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired();

            entity.Property(r => r.ExpiresAt)
                .HasColumnName("expires_at")
                .IsRequired();

            entity.HasIndex(r => new { r.ActorId, r.IdempotencyKey })
                .IsUnique()
                .HasDatabaseName("uq_idempotency_actor_key");

            entity.HasIndex(r => r.ExpiresAt)
                .HasDatabaseName("idx_idempotency_expires");
        });
    }

    private static void ConfigureReviewReports(ModelBuilder modelBuilder)
    {
        var statusConverter = new ValueConverter<ReportStatus, string>(
            v => v.ToString(),
            v => Enum.Parse<ReportStatus>(v));

        modelBuilder.Entity<ReviewReport>(entity =>
        {
            entity.ToTable("review_reports", t =>
            {
                t.HasCheckConstraint("chk_reports_status", "status IN ('PendingReview', 'ReviewedDismissed', 'ReviewedHidden')");
            });

            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.ReviewId).HasColumnName("review_id").IsRequired();
            entity.Property(r => r.ReporterId).HasColumnName("reporter_id").IsRequired();
            entity.Property(r => r.Reason).HasColumnName("reason").HasMaxLength(1000).IsRequired();
            entity.Property(r => r.Status).HasColumnName("status").HasConversion(statusConverter).HasMaxLength(32).IsRequired();
            entity.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(r => r.ResolvedAt).HasColumnName("resolved_at");
            entity.Property(r => r.ResolvedBy).HasColumnName("resolved_by");

            entity.HasIndex(r => new { r.ReviewId, r.ReporterId }).IsUnique().HasDatabaseName("uq_reports_review_reporter");
            entity.HasIndex(r => r.Status).HasDatabaseName("idx_reports_status");
        });
    }

    private static void ConfigureReviewProjections(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReviewProjection>(entity =>
        {
            entity.ToTable("review_projections");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).HasColumnName("id");
            entity.Property(p => p.OrderId).HasColumnName("order_id").IsRequired();
            entity.Property(p => p.ItemId).HasColumnName("item_id").IsRequired();
            entity.Property(p => p.RenterId).HasColumnName("renter_id").IsRequired();
            entity.Property(p => p.SupplierId).HasColumnName("supplier_id").IsRequired();
            entity.Property(p => p.EquipmentListingId).HasColumnName("equipment_listing_id").IsRequired();
            entity.Property(p => p.CompletedAt).HasColumnName("completed_at");
            entity.Property(p => p.DeadlineAt).HasColumnName("deadline_at");
            entity.Property(p => p.IsEligible).HasColumnName("is_eligible").IsRequired();
            entity.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();

            entity.HasIndex(p => p.ItemId).IsUnique().HasDatabaseName("uq_projections_item_id");
            entity.HasIndex(p => new { p.RenterId, p.IsEligible }).HasDatabaseName("idx_projections_renter_eligible");
            entity.HasIndex(p => new { p.SupplierId, p.IsEligible }).HasDatabaseName("idx_projections_supplier_eligible");
        });
    }

    private static void ConfigureInboxMessages(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("inbox_messages");
            entity.HasKey(m => new { m.MessageId, m.ConsumerGroup });
            entity.Property(m => m.MessageId).HasColumnName("message_id");
            entity.Property(m => m.ConsumerGroup).HasColumnName("consumer_group").HasMaxLength(128).IsRequired();
            entity.Property(m => m.MessageType).HasColumnName("message_type").HasMaxLength(128).IsRequired();
            entity.Property(m => m.OccurredAt).HasColumnName("occurred_at").IsRequired();
            entity.Property(m => m.ProcessedAt).HasColumnName("processed_at").IsRequired();
        });
    }

    private static void ConfigureReconciliationCheckpoints(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReconciliationCheckpoint>(entity =>
        {
            entity.ToTable("reconciliation_checkpoints");
            entity.HasKey(c => c.SweepId);
            entity.Property(c => c.SweepId).HasColumnName("sweep_id").HasMaxLength(64);
            entity.Property(c => c.WindowStart).HasColumnName("window_start").IsRequired();
            entity.Property(c => c.LastItemId).HasColumnName("last_item_id");
            entity.Property(c => c.IsCompleted).HasColumnName("is_completed").IsRequired();
            entity.Property(c => c.StartedAt).HasColumnName("started_at").IsRequired();
            entity.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();
        });
    }

    private static void ConfigureOutboxMessages(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Id).HasColumnName("id");
            entity.Property(o => o.Topic).HasColumnName("topic").HasMaxLength(128).IsRequired();
            entity.Property(o => o.EventType).HasColumnName("event_type").HasMaxLength(128).IsRequired();
            entity.Property(o => o.PartitionKey).HasColumnName("partition_key").HasMaxLength(128).IsRequired();
            entity.Property(o => o.Payload).HasColumnName("payload").IsRequired();
            entity.Property(o => o.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(o => o.ProcessedAt).HasColumnName("processed_at");

            entity.HasIndex(o => o.ProcessedAt).HasDatabaseName("idx_outbox_processed");
        });
    }

    private static void ConfigureReviewMedia(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReviewMedia>(entity =>
        {
            entity.ToTable("review_media");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id");
            entity.Property(m => m.ReviewId).HasColumnName("review_id").IsRequired();
            entity.Property(m => m.MediaId).HasColumnName("media_id").IsRequired();
            entity.Property(m => m.MediaType).HasColumnName("media_type").HasMaxLength(32).IsRequired();
            entity.Property(m => m.CreatedAt).HasColumnName("created_at").IsRequired();

            entity.HasIndex(m => new { m.ReviewId, m.MediaId }).IsUnique().HasDatabaseName("uq_review_media_pair");
        });
    }
}
