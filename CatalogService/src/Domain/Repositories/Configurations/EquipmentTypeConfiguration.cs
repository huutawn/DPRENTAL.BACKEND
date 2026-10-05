using Dprental.CatalogService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.CatalogService.Domain.Repositories.Configurations;

public sealed class EquipmentTypeConfiguration : IEntityTypeConfiguration<EquipmentType>
{
    public void Configure(EntityTypeBuilder<EquipmentType> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("EquipmentTypes", table =>
        {
            table.HasCheckConstraint(
                CatalogServiceDbContext.StatusCheckConstraintName,
                "\"Status\" IN ('Draft', 'Published', 'Disabled')");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.Code).IsRequired().HasMaxLength(50);
        builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Description).HasMaxLength(4000);

        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasColumnType("character varying(20)")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(e => e.PublishedAt).HasColumnType("timestamp with time zone");

        builder.Property<uint>("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("IX_EquipmentTypes_Code");
        builder.HasIndex(e => e.Status).HasDatabaseName("IX_EquipmentTypes_Status");
        builder.HasIndex(e => e.OrganizationId).HasDatabaseName("IX_EquipmentTypes_OrganizationId");
        builder.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_EquipmentTypes_CreatedAt");
        builder.HasIndex(e => e.PublishedAt).HasDatabaseName("IX_EquipmentTypes_PublishedAt");
        builder.HasIndex(e => e.ActiveSchemaVersionId).HasDatabaseName("IX_EquipmentTypes_ActiveSchemaVersionId");

        builder.HasMany(e => e.Categories)
            .WithMany(c => c.EquipmentTypes)
            .UsingEntity<Dictionary<string, object>>(
                "EquipmentTypeCategories",
                j => j.HasOne<Category>()
                      .WithMany()
                      .HasForeignKey("CategoryId")
                      .OnDelete(DeleteBehavior.Restrict)
                      .HasConstraintName("FK_EquipmentTypeCategories_Categories_CategoryId"),
                j => j.HasOne<EquipmentType>()
                      .WithMany()
                      .HasForeignKey("EquipmentTypeId")
                      .OnDelete(DeleteBehavior.Cascade)
                      .HasConstraintName("FK_EquipmentTypeCategories_EquipmentTypes_EquipmentTypeId"),
                j =>
                {
                    j.ToTable("EquipmentTypeCategories");
                    j.HasKey("EquipmentTypeId", "CategoryId").HasName("PK_EquipmentTypeCategories");
                    j.HasIndex("CategoryId").HasDatabaseName("IX_EquipmentTypeCategories_CategoryId");
                });

        builder.HasMany(e => e.Media)
            .WithOne(m => m.EquipmentType)
            .HasForeignKey(m => m.EquipmentTypeId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_EquipmentTypeMedia_EquipmentTypes_EquipmentTypeId");

        // NoAction để cascade xóa schema và version trong cùng câu lệnh không bị chặn
        builder.HasOne<SchemaVersion>()
            .WithMany()
            .HasForeignKey(e => e.ActiveSchemaVersionId)
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_EquipmentTypes_SchemaVersions_ActiveSchemaVersionId");
    }
}