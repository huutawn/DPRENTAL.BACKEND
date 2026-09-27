using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.MediaService.Domain.Configurations;

public class MediaCleanupItemConfiguration : IEntityTypeConfiguration<MediaCleanupItem>
{
    public void Configure(EntityTypeBuilder<MediaCleanupItem> builder)
    {
        builder.ToTable("media_cleanup_items", MediaDbContext.SchemaName);

        builder.HasKey(c => c.Id);

        builder.Property(c => c.StorageKey)
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(c => c.Reason)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(c => c.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(c => c.RetryCount)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(c => c.CreatedAtUtc)
            .IsRequired();

        builder.Property(c => c.EligibleAtUtc)
            .IsRequired();

        builder.HasIndex(c => new { c.Status, c.EligibleAtUtc });
    }
}
