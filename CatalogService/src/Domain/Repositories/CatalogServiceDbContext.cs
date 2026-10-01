using Dprental.CatalogService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Repositories;

/// <summary>
/// DbContext cho CatalogService quản lý các bảng trong schema "catalog".
/// </summary>
public sealed class CatalogServiceDbContext : DbContext
{
    public const string SchemaName = "catalog";

    public const string MigrationsHistoryTableName = "__EFMigrationsHistory";

    public const string DailyPriceCheckConstraintName = "CK_Equipments_DailyPrice_NonNegative";

    public const string SpecificationsCheckConstraintName = "CK_Equipments_Specifications_IsObject";

    public const string SingleThumbnailIndexName = "UX_EquipmentMedia_EquipmentId_Thumbnail";

    public CatalogServiceDbContext(DbContextOptions<CatalogServiceDbContext> options) : base(options)
    {
    }

    public DbSet<Category> Categories { get; set; }
    public DbSet<Equipment> Equipments { get; set; }
    public DbSet<EquipmentMedia> EquipmentMedia { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(SchemaName);

        // Mapping chính nằm ở attribute trên entity; ở đây chỉ còn các rule mà attribute không diễn đạt được.
        modelBuilder.Entity<Equipment>(equipment =>
        {
            equipment.ToTable(table =>
            {
                table.HasCheckConstraint(DailyPriceCheckConstraintName, "\"DailyPrice\" >= 0");
                table.HasCheckConstraint(SpecificationsCheckConstraintName, "jsonb_typeof(\"Specifications\") = 'object'");
            });
            equipment.Property(e => e.Specifications).HasDefaultValueSql("'{}'::jsonb");
        });

        // Mỗi equipment có tối đa một thumbnail.
        modelBuilder.Entity<EquipmentMedia>()
            .HasIndex(m => m.EquipmentId)
            .IsUnique()
            .HasFilter("\"IsThumbnail\" = TRUE")
            .HasDatabaseName(SingleThumbnailIndexName);
    }
}
