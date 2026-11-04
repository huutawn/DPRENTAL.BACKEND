using Dprental.NotificationService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.NotificationService.Domain.Repositories.Configurations;

public sealed class NotificationAggregateWatermarkConfiguration : IEntityTypeConfiguration<NotificationAggregateWatermark>
{
    public void Configure(EntityTypeBuilder<NotificationAggregateWatermark> builder)
    {
        builder.ToTable("aggregate_watermarks", "notification");

        builder.HasKey(w => new { w.Scope, w.AggregateId, w.RecipientUserId });

        builder.Property(w => w.Scope).HasColumnName("scope").IsRequired().HasMaxLength(64).HasDefaultValue(NotificationWatermarkScope.RentalOrder);
        builder.Property(w => w.AggregateId).HasColumnName("aggregate_id").IsRequired();
        builder.Property(w => w.RecipientUserId).HasColumnName("recipient_user_id").IsRequired();
        builder.Property(w => w.ParentOrderId).HasColumnName("parent_order_id");
        builder.Property(w => w.LastOrderVersion).HasColumnName("last_order_version").IsRequired().HasDefaultValue(0L);
        builder.Property(w => w.LastMessageId).HasColumnName("last_message_id").HasMaxLength(128);
        builder.Property(w => w.IsTerminal).HasColumnName("is_terminal").IsRequired().HasDefaultValue(false);
        builder.Property(w => w.TerminalState).HasColumnName("terminal_state").HasMaxLength(32);
        builder.Property(w => w.IsPaid).HasColumnName("is_paid").IsRequired().HasDefaultValue(false);
        builder.Property(w => w.UpdatedAt).HasColumnName("updated_at").IsRequired().HasDefaultValueSql("NOW()");

        builder.HasIndex(w => new { w.Scope, w.AggregateId, w.RecipientUserId })
            .HasDatabaseName("ix_aggregate_watermarks_scope_aggregate_recipient");
        builder.HasIndex(w => new { w.Scope, w.AggregateId })
            .HasDatabaseName("ix_aggregate_watermarks_scope_aggregate");
        builder.HasIndex(w => w.ParentOrderId)
            .HasDatabaseName("ix_aggregate_watermarks_parent_order");
    }
}
