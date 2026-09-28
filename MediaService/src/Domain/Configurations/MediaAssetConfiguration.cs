using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.MediaService.Domain.Configurations;

public class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
    public void Configure(EntityTypeBuilder<MediaAsset> builder)
    {
        builder.ToTable("media_assets", MediaDbContext.SchemaName);

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(a => a.Visibility)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(a => a.OriginalFileName)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(a => a.DeclaredContentType)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(a => a.DeclaredSizeBytes)
            .IsRequired();

        builder.Property(a => a.DeclaredChecksumSha256)
            .HasMaxLength(64);

        builder.Property(a => a.ActualContentType)
            .HasMaxLength(100);

        builder.Property(a => a.ActualChecksumSha256)
            .HasMaxLength(64);

        builder.Property(a => a.StorageKey)
            .HasMaxLength(1000);

        builder.Property(a => a.CreatedByUserId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(a => a.RejectionReason)
            .HasMaxLength(500);

        builder.Property(a => a.CreatedAtUtc)
            .IsRequired();

        builder.Property(a => a.Version)
            .IsConcurrencyToken();

        builder.HasIndex(a => a.Status);
        builder.HasIndex(a => a.CreatedByUserId);
    }
}
