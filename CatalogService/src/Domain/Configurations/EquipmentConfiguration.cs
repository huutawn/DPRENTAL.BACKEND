using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.CatalogService.Domain.Configurations;

public class EquipmentConfiguration : IEntityTypeConfiguration<Equipment>
{
    public const string DailyPriceCheckConstraintName = "CK_equipments_DailyPrice_NonNegative";

    public const string SpecificationsCheckConstraintName = "CK_equipments_Specifications_IsObject";

    public void Configure(EntityTypeBuilder<Equipment> builder)
    {
        builder.ToTable("equipments", CatalogServiceDbContext.SchemaName, table =>
        {
            table.HasCheckConstraint(DailyPriceCheckConstraintName, "\"DailyPrice\" >= 0");
            table.HasCheckConstraint(
                SpecificationsCheckConstraintName,
                "jsonb_typeof(\"Specifications\") = 'object'");
        });

        builder.HasKey(e => e.Id);

        // Id do ứng dụng tự gán, để EF luôn INSERT entity mới thay vì đoán theo giá trị khóa.
        builder.Property(e => e.Id)
            .ValueGeneratedNever();

        builder.Property(e => e.OrganizationId)
            .IsRequired();

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(e => e.Code)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(e => e.Brand)
            .HasMaxLength(100);

        builder.Property(e => e.Description)
            .HasMaxLength(4000);

        builder.Property(e => e.DailyPrice)
            .IsRequired()
            .HasPrecision(18, 2);

        builder.Property(e => e.RegionId)
            .IsRequired();

        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(e => e.Specifications)
            .IsRequired()
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb");

        builder.Property(e => e.CreatedAtUtc)
            .IsRequired();

        builder.HasOne(e => e.Category)
            .WithMany(c => c.Equipments)
            .HasForeignKey(e => e.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // Code chỉ duy nhất trong phạm vi organization; index này cũng phục vụ truy vấn theo OrganizationId.
        builder.HasIndex(e => new { e.OrganizationId, e.Code })
            .IsUnique();

        builder.HasIndex(e => e.Status);
        builder.HasIndex(e => e.CategoryId);
        builder.HasIndex(e => e.RegionId);
        builder.HasIndex(e => e.DailyPrice);
        builder.HasIndex(e => e.CreatedAtUtc);
        builder.HasIndex(e => e.PublishedAtUtc);
    }
}
