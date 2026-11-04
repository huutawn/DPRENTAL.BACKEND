using Dprental.NotificationService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.NotificationService.Domain.Repositories.Configurations;

public sealed class NotificationDeliveryJobConfiguration : IEntityTypeConfiguration<NotificationDeliveryJob>
{
    public void Configure(EntityTypeBuilder<NotificationDeliveryJob> builder)
    {
        builder.ToTable("delivery_jobs", "notification");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.Id).HasColumnName("id");
        builder.Property(j => j.NotificationId).HasColumnName("notification_id").IsRequired();
        builder.Property(j => j.Channel).HasColumnName("channel").HasMaxLength(32).IsRequired();
        builder.Property(j => j.RecipientUserId).HasColumnName("recipient_user_id").IsRequired();
        builder.Property(j => j.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
        builder.Property(j => j.AttemptCount).HasColumnName("attempt_count").IsRequired().HasDefaultValue(0);
        builder.Property(j => j.MaxAttempts).HasColumnName("max_attempts").IsRequired().HasDefaultValue(5);
        builder.Property(j => j.NextAttemptAt).HasColumnName("next_attempt_at").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(j => j.LeaseToken).HasColumnName("lease_token").HasMaxLength(64);
        builder.Property(j => j.LeaseExpiresAt).HasColumnName("lease_expires_at");
        builder.Property(j => j.ProviderMessageId).HasColumnName("provider_message_id").HasMaxLength(128);
        builder.Property(j => j.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(64);
        builder.Property(j => j.LastErrorDetail).HasColumnName("last_error_detail").HasColumnType("text");
        builder.Property(j => j.DispatchStartedAt).HasColumnName("dispatch_started_at");
        builder.Property(j => j.SentAt).HasColumnName("sent_at");
        builder.Property(j => j.CreatedAt).HasColumnName("created_at").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(j => j.UpdatedAt).HasColumnName("updated_at").IsRequired().HasDefaultValueSql("NOW()");

        builder.HasIndex(j => new { j.NotificationId, j.Channel })
            .IsUnique()
            .HasDatabaseName("uq_delivery_jobs_notification_channel");

        builder.HasIndex(j => new { j.Status, j.NextAttemptAt })
            .HasFilter("status IN ('Pending', 'RetryScheduled')")
            .HasDatabaseName("ix_delivery_jobs_pending_claim");

        builder.HasIndex(j => new { j.Status, j.LeaseExpiresAt })
            .HasFilter("status = 'Processing'")
            .HasDatabaseName("ix_delivery_jobs_lease_recovery");
    }
}
