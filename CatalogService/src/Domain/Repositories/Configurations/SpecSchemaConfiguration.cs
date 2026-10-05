using Dprental.CatalogService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.CatalogService.Domain.Repositories.Configurations;

public sealed class SpecSchemaConfiguration : IEntityTypeConfiguration<SpecSchema>
{
    public void Configure(EntityTypeBuilder<SpecSchema> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("SpecSchemas");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        // Quan hệ 1-1, EF tự tạo unique index trên EquipmentTypeId
        builder.HasOne<EquipmentType>()
            .WithOne(type => type.Schema)
            .HasForeignKey<SpecSchema>(x => x.EquipmentTypeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}