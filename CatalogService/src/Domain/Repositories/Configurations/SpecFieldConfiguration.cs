using Dprental.CatalogService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.CatalogService.Domain.Repositories.Configurations;

public sealed class SpecFieldConfiguration : IEntityTypeConfiguration<SpecField>
{
    public void Configure(EntityTypeBuilder<SpecField> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("SpecFields", table => table.HasCheckConstraint(
            "CK_SpecFields_MinMax",
            "\"MinValue\" IS NULL OR \"MaxValue\" IS NULL OR \"MinValue\" <= \"MaxValue\""));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Key).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Label).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Unit).HasMaxLength(50);
        builder.Property(x => x.DataType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.MinValue).HasPrecision(18, 4);
        builder.Property(x => x.MaxValue).HasPrecision(18, 4);

        builder.HasIndex(x => new { x.SchemaVersionId, x.Key }).IsUnique();
        builder.HasIndex(x => new { x.SchemaVersionId, x.DisplayOrder });

        builder.HasOne<SchemaVersion>()
            .WithMany(version => version.Fields)
            .HasForeignKey(x => x.SchemaVersionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}