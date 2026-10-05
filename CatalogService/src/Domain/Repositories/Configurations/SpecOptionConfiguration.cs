using Dprental.CatalogService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.CatalogService.Domain.Repositories.Configurations;

public sealed class SpecOptionConfiguration : IEntityTypeConfiguration<SpecOption>
{
    public void Configure(EntityTypeBuilder<SpecOption> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("SpecOptions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Value).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Label).HasMaxLength(200).IsRequired();

        builder.HasIndex(x => new { x.SpecFieldId, x.Value }).IsUnique();

        builder.HasOne<SpecField>()
            .WithMany(field => field.Options)
            .HasForeignKey(x => x.SpecFieldId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}