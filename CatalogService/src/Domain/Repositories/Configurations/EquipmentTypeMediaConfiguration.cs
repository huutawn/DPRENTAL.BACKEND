using Dprental.CatalogService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.CatalogService.Domain.Repositories.Configurations;

public sealed class EquipmentTypeMediaConfiguration : IEntityTypeConfiguration<EquipmentTypeMedia>
{
    public void Configure(EntityTypeBuilder<EquipmentTypeMedia> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("EquipmentTypeMedia");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.EquipmentTypeId).IsRequired();
        builder.Property(m => m.MediaId).IsRequired();
        builder.Property(m => m.DisplayOrder).IsRequired();
        builder.Property(m => m.IsThumbnail).IsRequired();

        builder.HasIndex(m => new { m.EquipmentTypeId, m.MediaId })
            .IsUnique()
            .HasDatabaseName("IX_EquipmentTypeMedia_EquipmentTypeId_MediaId");

        // Mỗi equipment type chỉ có tối đa một ảnh thumbnail
        builder.HasIndex(m => m.EquipmentTypeId)
            .IsUnique()
            .HasDatabaseName("UX_EquipmentTypeMedia_EquipmentTypeId_Thumbnail")
            .HasFilter("\"IsThumbnail\" = TRUE");
    }
}