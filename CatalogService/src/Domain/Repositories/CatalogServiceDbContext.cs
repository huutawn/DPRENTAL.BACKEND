using Dprental.CatalogService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Repositories;

public sealed class CatalogServiceDbContext : DbContext
{
    public const string SchemaName = "catalog";
    public const string MigrationsHistoryTableName = "__EFMigrationsHistory";

    public const string StatusCheckConstraintName = "CK_EquipmentTypes_Status_Canonical";

    public DbSet<EquipmentType> EquipmentTypes { get; set; }
    public DbSet<EquipmentTypeMedia> EquipmentTypeMedia { get; set; }
    public DbSet<Category> Categories { get; set; }

    public CatalogServiceDbContext(DbContextOptions<CatalogServiceDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<EquipmentType>(entity =>
        {
            entity.ToTable("EquipmentTypes", SchemaName, table =>
            {
                table.HasCheckConstraint(StatusCheckConstraintName, "\"Status\" IN ('Draft', 'Published', 'Disabled')");
            });

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.Code)
                .IsRequired()
                .HasMaxLength(50);

            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(e => e.Description)
                .HasMaxLength(4000);

            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasColumnType("character varying(20)")
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(e => e.CreatedAt)
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp with time zone");

            entity.Property(e => e.PublishedAt)
                .HasColumnType("timestamp with time zone");

            entity.Property<uint>("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            entity.HasIndex(e => e.Code)
                .IsUnique()
                .HasDatabaseName("IX_EquipmentTypes_Code");

            entity.HasIndex(e => e.CategoryId)
                .HasDatabaseName("IX_EquipmentTypes_CategoryId");

            entity.HasIndex(e => e.Status)
                .HasDatabaseName("IX_EquipmentTypes_Status");

            entity.HasIndex(e => e.CreatedAt)
                .HasDatabaseName("IX_EquipmentTypes_CreatedAt");

            entity.HasIndex(e => e.PublishedAt)
                .HasDatabaseName("IX_EquipmentTypes_PublishedAt");

            entity.HasOne(e => e.Category)
                .WithMany(c => c.EquipmentTypes)
                .HasForeignKey(e => e.CategoryId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_EquipmentTypes_Categories_CategoryId");

            entity.HasMany(e => e.Media)
                .WithOne(m => m.EquipmentType)
                .HasForeignKey(m => m.EquipmentTypeId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_EquipmentTypeMedia_EquipmentTypes_EquipmentTypeId");
        });

        modelBuilder.Entity<EquipmentTypeMedia>(entity =>
        {
            entity.ToTable("EquipmentTypeMedia", SchemaName);

            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).ValueGeneratedNever();

            entity.Property(m => m.EquipmentTypeId).IsRequired();
            entity.Property(m => m.MediaId).IsRequired();
            entity.Property(m => m.DisplayOrder).IsRequired();
            entity.Property(m => m.IsThumbnail).IsRequired();

            entity.HasIndex(m => new { m.EquipmentTypeId, m.MediaId })
                .IsUnique()
                .HasDatabaseName("IX_EquipmentTypeMedia_EquipmentTypeId_MediaId");

            entity.HasIndex(m => m.EquipmentTypeId)
                .IsUnique()
                .HasDatabaseName("UX_EquipmentTypeMedia_EquipmentTypeId_Thumbnail")
                .HasFilter("\"IsThumbnail\" = TRUE");
        });
    }
}
