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

        builder.Property(w => w.Scope).IsRequired().HasMaxLength(64).HasDefaultValue("RentalOrder");
        builder.Property(w => w.LastOrderVersion).IsRequired().HasDefaultValue(0L);
        builder.Property(w => w.LastMessageId).HasMaxLength(128);
        builder.Property(w => w.IsTerminal).IsRequired().HasDefaultValue(false);
        builder.Property(w => w.TerminalState).HasMaxLength(32);
        builder.Property(w => w.IsPaid).IsRequired().HasDefaultValue(false);
        builder.Property(w => w.UpdatedAt).IsRequired().HasDefaultValueSql("NOW()");

        builder.HasIndex(w => new { w.Scope, w.AggregateId, w.RecipientUserId });
        builder.HasIndex(w => new { w.Scope, w.AggregateId });
    }
}
