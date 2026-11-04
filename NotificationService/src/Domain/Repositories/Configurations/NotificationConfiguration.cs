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

        builder.Property(n => n.Id).HasColumnName("id");
        builder.Property(n => n.RecipientUserId).HasColumnName("recipient_user_id").IsRequired();
        builder.Property(n => n.OrderId).HasColumnName("order_id");
        builder.Property(n => n.RentalItemId).HasColumnName("rental_item_id");
        builder.Property(n => n.NotificationType).HasColumnName("notification_type").HasMaxLength(64).IsRequired();
        builder.Property(n => n.Category).HasColumnName("category").HasMaxLength(32).IsRequired();
        builder.Property(n => n.Severity).HasColumnName("severity").HasMaxLength(16).IsRequired();
        builder.Property(n => n.Title).HasColumnName("title").HasMaxLength(256).IsRequired();
        builder.Property(n => n.Body).HasColumnName("body").IsRequired();
        builder.Property(n => n.ResourceType).HasColumnName("resource_type").HasMaxLength(64).IsRequired();
        builder.Property(n => n.ResourceId).HasColumnName("resource_id").HasMaxLength(128).IsRequired();
        builder.Property(n => n.SourceEventId).HasColumnName("source_event_id").IsRequired();
        builder.Property(n => n.SourceProducer).HasColumnName("source_producer").HasMaxLength(64).IsRequired();
        builder.Property(n => n.SourceEventType).HasColumnName("source_event_type").HasMaxLength(64).IsRequired();
        builder.Property(n => n.EventVersion).HasColumnName("event_version").IsRequired().HasDefaultValue(0L);
        builder.Property(n => n.TemplateCode).HasColumnName("template_code").HasMaxLength(64).IsRequired();
        builder.Property(n => n.TemplateVersion).HasColumnName("template_version").HasMaxLength(32).IsRequired();
        builder.Property(n => n.RenderDataJson).HasColumnName("render_data_json").HasColumnType("text");
        builder.Property(n => n.DeepLinkUrl).HasColumnName("deep_link_url").HasMaxLength(512);
        builder.Property(n => n.IsActionable).HasColumnName("is_actionable").IsRequired().HasDefaultValue(false);
        builder.Property(n => n.ActionUrl).HasColumnName("action_url").HasMaxLength(512);
        builder.Property(n => n.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(n => n.CreatedAt).HasColumnName("created_at").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(n => n.ReadAt).HasColumnName("read_at");

        builder.HasIndex(n => new { n.SourceEventId, n.RecipientUserId, n.NotificationType })
            .IsUnique()
            .HasDatabaseName("uq_notifications_source_recipient_type");

        builder.HasIndex(n => new { n.RecipientUserId, n.CreatedAt, n.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_notifications_recipient_created");

        builder.HasIndex(n => new { n.RecipientUserId, n.ReadAt })
            .HasFilter("read_at IS NULL")
            .HasDatabaseName("ix_notifications_recipient_unread");

        builder.HasIndex(n => n.OrderId)
            .HasDatabaseName("ix_notifications_order_id");

        builder.HasIndex(n => n.RentalItemId)
            .HasDatabaseName("ix_notifications_rental_item");

        builder.HasMany(n => n.DeliveryJobs)
            .WithOne(j => j.Notification)
            .HasForeignKey(j => j.NotificationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
