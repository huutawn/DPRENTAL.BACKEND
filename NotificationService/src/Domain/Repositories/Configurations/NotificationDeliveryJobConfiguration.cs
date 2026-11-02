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

        builder.Property(j => j.NotificationId).IsRequired();
        builder.Property(j => j.Channel).HasMaxLength(32).IsRequired();
        builder.Property(j => j.RecipientUserId).IsRequired();
        builder.Property(j => j.Status).HasMaxLength(32).IsRequired();
        builder.Property(j => j.AttemptCount).IsRequired().HasDefaultValue(0);
        builder.Property(j => j.MaxAttempts).IsRequired().HasDefaultValue(5);
        builder.Property(j => j.NextAttemptAt).IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(j => j.LeaseToken).HasMaxLength(64);
        builder.Property(j => j.LeaseExpiresAt);
        builder.Property(j => j.ProviderMessageId).HasMaxLength(128);
        builder.Property(j => j.LastErrorCode).HasMaxLength(64);
        builder.Property(j => j.LastErrorDetail).HasColumnType("text");
        builder.Property(j => j.DispatchStartedAt);
        builder.Property(j => j.SentAt);
        builder.Property(j => j.CreatedAt).IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(j => j.UpdatedAt).IsRequired().HasDefaultValueSql("NOW()");

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
