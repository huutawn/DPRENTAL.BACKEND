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

        builder.Property(p => p.Id).HasColumnName("id");
        builder.Property(p => p.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(p => p.Category).HasColumnName("category").HasMaxLength(32).IsRequired();
        builder.Property(p => p.EmailEnabled).HasColumnName("email_enabled").IsRequired().HasDefaultValue(true);
        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired().HasDefaultValueSql("NOW()");

        builder.HasIndex(p => new { p.UserId, p.Category })
            .IsUnique()
            .HasDatabaseName("uq_preferences_user_category");

        builder.HasIndex(p => p.UserId)
            .HasDatabaseName("ix_preferences_user_id");
    }
}
