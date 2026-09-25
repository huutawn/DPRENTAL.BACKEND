using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.MediaService.Domain.Configurations;

public class MediaUploadSessionConfiguration : IEntityTypeConfiguration<MediaUploadSession>
{
    public void Configure(EntityTypeBuilder<MediaUploadSession> builder)
    {
        builder.ToTable("media_upload_sessions", MediaDbContext.SchemaName);

        builder.HasKey(s => s.Id);

        builder.Property(s => s.CallerIdentity)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(s => s.IdempotencyKey)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(s => s.RequestPayloadHash)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(s => s.TemporaryStorageKey)
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(s => s.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(s => s.ExpiresAtUtc)
            .IsRequired();

        builder.Property(s => s.CreatedAtUtc)
            .IsRequired();

        builder.Property(s => s.Version)
            .IsConcurrencyToken();

        builder.HasOne(s => s.MediaAsset)
            .WithMany(a => a.UploadSessions)
            .HasForeignKey(s => s.MediaAssetId)
            .OnDelete(DeleteBehavior.Cascade);

        // Ràng buộc duy nhất cho (caller, Idempotency-Key)
        builder.HasIndex(s => new { s.CallerIdentity, s.IdempotencyKey })
            .IsUnique();

        builder.HasIndex(s => s.Status);
        builder.HasIndex(s => s.ExpiresAtUtc);
    }
}
