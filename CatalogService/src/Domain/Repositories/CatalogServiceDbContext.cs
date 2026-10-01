using Dprental.CatalogService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Repositories;

public sealed class CatalogServiceDbContext : DbContext
{
    public const string SchemaName = "catalog";
    public const string MigrationsHistoryTableName = "__EFMigrationsHistory";

    public const string DailyPriceCheckConstraintName = "CK_Equipments_DailyPrice_NonNegative";
    public const string SpecificationsCheckConstraintName = "CK_Equipments_Specifications_IsObject";
    public DbSet<Equipment> Equipments { get; set; }
    public DbSet<Category> Categories { get; set; }
    public CatalogServiceDbContext(DbContextOptions<CatalogServiceDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Equipment>(entity =>
        {
            entity.Property(e => e.Specifications)
                .HasDefaultValueSql("'{}'::jsonb");
            entity.ToTable("Equipments", SchemaName, table =>
            {
                table.HasCheckConstraint(DailyPriceCheckConstraintName, "\"DailyPrice\" >= 0");
                table.HasCheckConstraint(SpecificationsCheckConstraintName, "jsonb_typeof(\"Specifications\") = 'object'");
            });
        });

        modelBuilder.Entity<EquipmentMedia>()
            .HasIndex(media => media.EquipmentId)
            .IsUnique()
            .HasDatabaseName("UX_EquipmentMedia_EquipmentId_Thumbnail")
            .HasFilter("\"IsThumbnail\" = TRUE");
    }
}
