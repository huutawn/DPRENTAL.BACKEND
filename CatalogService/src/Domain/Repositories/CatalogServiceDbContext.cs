using Dprental.CatalogService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Repositories;

public sealed class CatalogServiceDbContext(DbContextOptions<CatalogServiceDbContext> options) : DbContext(options)
{
    public const string SchemaName = "catalog";
    public const string MigrationsHistoryTableName = "__EFMigrationsHistory";
    public const string StatusCheckConstraintName = "CK_EquipmentTypes_Status_Canonical";

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<EquipmentType> EquipmentTypes => Set<EquipmentType>();
    public DbSet<EquipmentTypeMedia> EquipmentTypeMedia => Set<EquipmentTypeMedia>();
    public DbSet<SpecSchema> SpecSchemas => Set<SpecSchema>();
    public DbSet<SchemaVersion> SchemaVersions => Set<SchemaVersion>();
    public DbSet<SpecField> SpecFields => Set<SpecField>();
    public DbSet<SpecOption> SpecOptions => Set<SpecOption>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DomainAssemblyMarker).Assembly);
    }
}