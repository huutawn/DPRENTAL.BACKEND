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

    public CatalogServiceDbContext(DbContextOptions<CatalogServiceDbContext> options) : base(options)
    {
    }

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Equipment> Equipments => Set<Equipment>();
    public DbSet<EquipmentMedia> EquipmentMedia => Set<EquipmentMedia>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogServiceDbContext).Assembly);
    }
}
