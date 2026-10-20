using Dprental.MediaService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.MediaService.Domain.Configurations;

public class MediaImageVariantConfiguration : IEntityTypeConfiguration<MediaImageVariant>
{
    public void Configure(EntityTypeBuilder<MediaImageVariant> builder)
    {
        builder.ToTable("media_image_variants", MediaDbContext.SchemaName);

        builder.HasKey(v => v.Id);

        builder.Property(v => v.VariantName)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(v => v.ProcessingVersion)
            .IsRequired();

        builder.Property(v => v.StorageKey)
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(v => v.ContentType)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(v => v.Width)
            .IsRequired();

        builder.Property(v => v.Height)
            .IsRequired();

        builder.Property(v => v.SizeBytes)
            .IsRequired();

        builder.Property(v => v.ChecksumSha256)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(v => v.CreatedAtUtc)
            .IsRequired();

        builder.HasIndex(v => new { v.MediaAssetId, v.ProcessingVersion, v.VariantName })
            .IsUnique();

        builder.HasOne(v => v.MediaAsset)
            .WithMany(a => a.ImageVariants)
            .HasForeignKey(v => v.MediaAssetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
