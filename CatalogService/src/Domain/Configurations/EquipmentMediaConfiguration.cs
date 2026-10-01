using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.CatalogService.Domain.Configurations;

public class EquipmentMediaConfiguration : IEntityTypeConfiguration<EquipmentMedia>
{
    public const string SingleThumbnailIndexName = "UX_equipment_media_EquipmentId_Thumbnail";

    public void Configure(EntityTypeBuilder<EquipmentMedia> builder)
    {
        builder.ToTable("equipment_media", CatalogServiceDbContext.SchemaName);

        builder.HasKey(m => m.Id);

        // Id do ứng dụng tự gán, để media mới thêm vào equipment đã load luôn được INSERT.
        builder.Property(m => m.Id)
            .ValueGeneratedNever();

        builder.Property(m => m.MediaId)
            .IsRequired();

        builder.Property(m => m.DisplayOrder)
            .IsRequired();

        builder.Property(m => m.IsThumbnail)
            .IsRequired();

        builder.HasOne(m => m.Equipment)
            .WithMany(e => e.Media)
            .HasForeignKey(m => m.EquipmentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Một media chỉ được gắn một lần vào cùng một equipment.
        builder.HasIndex(m => new { m.EquipmentId, m.MediaId })
            .IsUnique();

        // Mỗi equipment có tối đa một thumbnail.
        builder.HasIndex(m => m.EquipmentId)
            .IsUnique()
            .HasFilter("\"IsThumbnail\" = TRUE")
            .HasDatabaseName(SingleThumbnailIndexName);
    }
}
