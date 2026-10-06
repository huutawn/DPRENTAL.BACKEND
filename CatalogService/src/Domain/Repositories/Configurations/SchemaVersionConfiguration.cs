using Dprental.CatalogService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.CatalogService.Domain.Repositories.Configurations;

public sealed class SchemaVersionConfiguration : IEntityTypeConfiguration<SchemaVersion>
{
    public void Configure(EntityTypeBuilder<SchemaVersion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("SchemaVersions", table => table.HasCheckConstraint(
    "CK_SchemaVersions_Status", "\"Status\" IN ('Draft', 'Published', 'Deprecated')"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(x => new { x.SpecSchemaId, x.VersionNumber }).IsUnique();

        // Mỗi schema chỉ có tối đa một version Draft, database cũng đảm bảo điều này
        builder.HasIndex(x => x.SpecSchemaId)
            .IsUnique()
            .HasFilter("\"Status\" = 'Draft'")
            .HasDatabaseName("UX_SchemaVersions_OneDraftPerSchema");

        builder.HasOne(x => x.SpecSchema)
            .WithMany(schema => schema.Versions)
            .HasForeignKey(x => x.SpecSchemaId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}