using Dprental.NotificationService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.NotificationService.Domain.Repositories.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications", "notification");

        builder.HasKey(n => n.Id);

        builder.Property(n => n.RecipientUserId).IsRequired();
        builder.Property(n => n.NotificationType).HasMaxLength(64).IsRequired();
        builder.Property(n => n.Category).HasMaxLength(32).IsRequired();
        builder.Property(n => n.Severity).HasMaxLength(16).IsRequired();
        builder.Property(n => n.Title).HasMaxLength(256).IsRequired();
        builder.Property(n => n.Body).IsRequired();
        builder.Property(n => n.ResourceType).HasMaxLength(64).IsRequired();
        builder.Property(n => n.ResourceId).HasMaxLength(128).IsRequired();
        builder.Property(n => n.SourceEventId).IsRequired();
        builder.Property(n => n.SourceProducer).HasMaxLength(64).IsRequired();
        builder.Property(n => n.SourceEventType).HasMaxLength(64).IsRequired();
        builder.Property(n => n.EventVersion).IsRequired().HasDefaultValue(0L);
        builder.Property(n => n.TemplateCode).HasMaxLength(64).IsRequired();
        builder.Property(n => n.TemplateVersion).HasMaxLength(32).IsRequired();
        builder.Property(n => n.RenderDataJson).HasColumnType("text");
        builder.Property(n => n.DeepLinkUrl).HasMaxLength(512);
        builder.Property(n => n.IsActionable).IsRequired().HasDefaultValue(false);
        builder.Property(n => n.ActionUrl).HasMaxLength(512);
        builder.Property(n => n.OccurredAt).IsRequired();
        builder.Property(n => n.CreatedAt).IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(n => n.ReadAt);

        builder.HasIndex(n => new { n.SourceEventId, n.RecipientUserId, n.NotificationType })
            .IsUnique()
            .HasDatabaseName("uq_notifications_source_recipient_type");

        builder.HasIndex(n => new { n.RecipientUserId, n.CreatedAt, n.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_notifications_recipient_created");

        builder.HasIndex(n => new { n.RecipientUserId, n.ReadAt })
            .HasFilter("read_at IS NULL")
            .HasDatabaseName("ix_notifications_recipient_unread");

        builder.HasMany(n => n.DeliveryJobs)
            .WithOne(j => j.Notification)
            .HasForeignKey(j => j.NotificationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
