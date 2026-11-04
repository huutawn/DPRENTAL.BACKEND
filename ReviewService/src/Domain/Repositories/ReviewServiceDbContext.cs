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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureReviews(modelBuilder);
        ConfigureReviewAudits(modelBuilder);
        ConfigureIdempotencyRecords(modelBuilder);
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
}
