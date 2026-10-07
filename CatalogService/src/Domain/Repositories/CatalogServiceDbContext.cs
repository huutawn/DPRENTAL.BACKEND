using System.Text.Json;
using Dprental.CatalogService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Repositories;

public sealed class CatalogServiceDbContext(DbContextOptions<CatalogServiceDbContext> options) : DbContext(options)
{
    public const string SchemaName = "catalog";
    public const string MigrationsHistoryTableName = "__EFMigrationsHistory";
    public const string StatusCheckConstraintName = "CK_EquipmentTypes_Status_Canonical";

    public DbSet<EquipmentType> EquipmentTypes { get; set; }
    public DbSet<EquipmentTypeMedia> EquipmentTypeMedia { get; set; }
    public DbSet<Category> Categories { get; set; }
    public DbSet<EquipmentTypeSpecSchema> EquipmentTypeSpecSchemas { get; set; }
    public DbSet<Brand> Brands { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
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

            entity.Property(e => e.NormalizedName)
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

            entity.Property(e => e.CurrentSchemaVersion);
            entity.Property<uint>("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            entity.HasIndex(e => e.Code)
                .IsUnique()
                .HasDatabaseName("IX_EquipmentTypes_Code");


            entity.HasIndex(e => e.NormalizedName)
                .HasDatabaseName("IX_EquipmentTypes_NormalizedName");

            entity.HasIndex(e => e.Status)
                .HasDatabaseName("IX_EquipmentTypes_Status");

            entity.HasIndex(e => e.CreatedAt)
                .HasDatabaseName("IX_EquipmentTypes_CreatedAt");

            entity.HasIndex(e => e.PublishedAt)
                .HasDatabaseName("IX_EquipmentTypes_PublishedAt");

            entity.HasMany(e => e.Categories)
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
                        j.ToTable("EquipmentTypeCategories", SchemaName);
                        j.HasKey("EquipmentTypeId", "CategoryId").HasName("PK_EquipmentTypeCategories");
                        j.HasIndex("CategoryId").HasDatabaseName("IX_EquipmentTypeCategories_CategoryId");
                    });

            entity.HasMany(e => e.Media)
                .WithOne(m => m.EquipmentType)
                .HasForeignKey(m => m.EquipmentTypeId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_EquipmentTypeMedia_EquipmentTypes_EquipmentTypeId");

            entity.HasOne<EquipmentTypeSpecSchema>()
                .WithMany()
                .HasForeignKey(e => new { e.Id, e.CurrentSchemaVersion })
                .HasPrincipalKey(s => new { s.EquipmentTypeId, s.Version })
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false)
                .HasConstraintName("FK_EquipmentTypes_EquipmentTypeSpecSchemas_CurrentSchema");
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

        modelBuilder.Entity<EquipmentTypeSpecSchema>(entity =>
        {
            entity.ToTable("EquipmentTypeSpecSchemas", SchemaName, table =>
            {
                table.HasCheckConstraint("CK_EquipmentTypeSpecSchemas_Version_Positive", "\"Version\" > 0");
            });

            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedNever();

            entity.Property(s => s.EquipmentTypeId).IsRequired();
            entity.Property(s => s.Version).IsRequired();

            entity.Property(s => s.Definition)
                .HasColumnType("jsonb")
                .IsRequired()
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<EquipmentTypeSpecDefinition>(v, JsonOptions)!);

            entity.Property(s => s.CreatedAt)
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(s => s.PublishedAt)
                .HasColumnType("timestamp with time zone");

            entity.HasOne(s => s.EquipmentType)
                .WithMany(e => e.SpecSchemas)
                .HasForeignKey(s => s.EquipmentTypeId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_EquipmentTypeSpecSchemas_EquipmentTypes_EquipmentTypeId");

            entity.HasAlternateKey(s => new { s.EquipmentTypeId, s.Version })
                .HasName("AK_EquipmentTypeSpecSchemas_EquipmentTypeId_Version");

            entity.HasIndex(s => s.EquipmentTypeId)
                .IsUnique()
                .HasDatabaseName("UX_EquipmentTypeSpecSchemas_EquipmentTypeId_Draft")
                .HasFilter("\"PublishedAt\" IS NULL");
        });

        modelBuilder.Entity<Brand>(entity =>
        {
            entity.ToTable("Brands", SchemaName);

            entity.HasKey(b => b.Id);
            entity.Property(b => b.Id).ValueGeneratedNever();

            entity.Property(b => b.Code)
                .IsRequired()
                .HasMaxLength(50);

            entity.Property(b => b.Name)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(b => b.Slug)
                .IsRequired()
                .HasMaxLength(150);

            entity.Property(b => b.LogoMediaId);

            entity.Property(b => b.IsActive)
                .IsRequired();

            entity.Property(b => b.CreatedAt)
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(b => b.UpdatedAt)
                .HasColumnType("timestamp with time zone");

            entity.Property<uint>("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            entity.HasIndex(b => b.Code)
                .IsUnique()
                .HasDatabaseName("IX_Brands_Code");

            entity.HasIndex(b => b.Slug)
                .IsUnique()
                .HasDatabaseName("IX_Brands_Slug");

            entity.HasIndex(b => b.CreatedAt)
                .HasDatabaseName("IX_Brands_CreatedAt");

            entity.HasIndex(b => b.IsActive)
                .HasDatabaseName("IX_Brands_IsActive");
        });
    }
}
