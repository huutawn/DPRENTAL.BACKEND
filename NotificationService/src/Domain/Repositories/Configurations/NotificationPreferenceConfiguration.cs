using Dprental.NotificationService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.NotificationService.Domain.Repositories.Configurations;

public sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("preferences", "notification");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.UserId).IsRequired();
        builder.Property(p => p.Category).HasMaxLength(32).IsRequired();
        builder.Property(p => p.EmailEnabled).IsRequired().HasDefaultValue(true);
        builder.Property(p => p.CreatedAt).IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(p => p.UpdatedAt).IsRequired().HasDefaultValueSql("NOW()");

        builder.HasIndex(p => new { p.UserId, p.Category })
            .IsUnique()
            .HasDatabaseName("uq_preferences_user_category");

        builder.HasIndex(p => p.UserId)
            .HasDatabaseName("ix_preferences_user_id");
    }
}
