using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

/// <summary>
/// Kiểm tra relational model của Catalog mà không cần kết nối PostgreSQL thật.
/// </summary>
public sealed class CatalogSchemaTests
{
    [Fact]
    public void Tables_LiveInCatalogSchema()
    {
        IModel model = BuildModel();

        Assert.All(model.GetEntityTypes(), entity => Assert.Equal(CatalogServiceDbContext.SchemaName, entity.GetSchema()));
        Assert.Equal("Categories", Entity<Category>(model).GetTableName());
        Assert.Equal("EquipmentTypes", Entity<EquipmentType>(model).GetTableName());
        Assert.Equal("EquipmentTypeMedia", Entity<EquipmentTypeMedia>(model).GetTableName());
        Assert.Equal("EquipmentTypeSpecSchemas", Entity<EquipmentTypeSpecSchema>(model).GetTableName());
        Assert.Equal("EquipmentTypeCategories", model.FindEntityType("EquipmentTypeCategories")?.GetTableName());
        Assert.Equal("Brands", Entity<Brand>(model).GetTableName());
    }

    [Fact]
    public void CategorySlug_IsUnique()
    {
        IIndex index = SingleIndex(Entity<Category>(BuildModel()), nameof(Category.Slug));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void EquipmentTypeCode_IsUnique()
    {
        IIndex index = SingleIndex(
            Entity<EquipmentType>(BuildModel()),
            nameof(EquipmentType.Code));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void BrandCode_IsUnique()
    {
        IIndex index = SingleIndex(
            Entity<Brand>(BuildModel()),
            nameof(Brand.Code));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void BrandSlug_IsUnique()
    {
        IIndex index = SingleIndex(
            Entity<Brand>(BuildModel()),
            nameof(Brand.Slug));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void Brand_UsesXminConcurrencyToken()
    {
        IEntityType brand = Entity<Brand>(BuildModel());
        IProperty? xmin = brand.FindProperty("xmin");

        Assert.NotNull(xmin);
        Assert.True(xmin.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, xmin.ValueGenerated);
    }

    [Theory]
    [InlineData(nameof(Brand.CreatedAt))]
    [InlineData(nameof(Brand.IsActive))]
    public void Brand_HasIndexOn(string propertyName)
    {
        IIndex index = SingleIndex(Entity<Brand>(BuildModel()), propertyName);

        Assert.False(index.IsUnique);
    }

    [Theory]
    [InlineData(nameof(EquipmentType.Status))]
    [InlineData(nameof(EquipmentType.CreatedAt))]
    [InlineData(nameof(EquipmentType.PublishedAt))]
    public void EquipmentType_HasIndexOn(string propertyName)
    {
        IIndex index = SingleIndex(Entity<EquipmentType>(BuildModel()), propertyName);

        Assert.False(index.IsUnique);
    }

    [Fact]
    public void EquipmentTypeCategories_HasIndexOnCategoryId()
    {
        IEntityType joinEntity = BuildModel().FindEntityType("EquipmentTypeCategories")!;
        IIndex index = SingleIndex(joinEntity, "CategoryId");
        Assert.False(index.IsUnique);
    }

    [Fact]
    public void EquipmentTypeStatus_IsStoredAsString()
    {
        IProperty property = Entity<EquipmentType>(BuildModel()).FindProperty(nameof(EquipmentType.Status))!;

        Assert.Equal("character varying(20)", property.GetColumnType());
        Assert.Equal(typeof(string), property.GetTypeMapping().Converter?.ProviderClrType);
    }

    [Fact]
    public void EquipmentType_HasStatusCheckConstraint()
    {
        IEntityType equipmentType = Entity<EquipmentType>(BuildModel());

        Assert.Contains(
            equipmentType.GetCheckConstraints(),
            constraint => constraint.Name == CatalogServiceDbContext.StatusCheckConstraintName);
    }

    [Fact]
    public void EquipmentType_UsesXminConcurrencyToken()
    {
        IEntityType equipmentType = Entity<EquipmentType>(BuildModel());
        IProperty? xmin = equipmentType.FindProperty("xmin");

        Assert.NotNull(xmin);
        Assert.True(xmin.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, xmin.ValueGenerated);
    }

    [Theory]
    [InlineData(typeof(Category))]
    [InlineData(typeof(EquipmentType))]
    [InlineData(typeof(EquipmentTypeMedia))]
    [InlineData(typeof(EquipmentTypeSpecSchema))]
    [InlineData(typeof(Brand))]
    public void Ids_AreAssignedByApplication(Type entityType)
    {
        IProperty id = BuildModel().FindEntityType(entityType)!.FindProperty("Id")!;

        Assert.Equal(ValueGenerated.Never, id.ValueGenerated);
    }

    [Fact]
    public void EquipmentTypeCategories_ConfiguresForeignKeysCorrectly()
    {
        IEntityType joinEntity = BuildModel().FindEntityType("EquipmentTypeCategories")!;
        IForeignKey categoryFk = Assert.Single(joinEntity.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(Category));
        Assert.Equal(DeleteBehavior.Restrict, categoryFk.DeleteBehavior);

        IForeignKey equipmentFk = Assert.Single(joinEntity.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(EquipmentType));
        Assert.Equal(DeleteBehavior.Cascade, equipmentFk.DeleteBehavior);
    }

    [Fact]
    public void EquipmentTypeMedia_BelongsToEquipmentType_AndIsDeletedWithIt()
    {
        IForeignKey foreignKey = Assert.Single(Entity<EquipmentTypeMedia>(BuildModel()).GetForeignKeys());

        Assert.Equal(typeof(EquipmentType), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void EquipmentTypeMedia_AllowsOneThumbnailPerEquipmentType()
    {
        IEntityType media = Entity<EquipmentTypeMedia>(BuildModel());

        IIndex thumbnail = SingleIndex(media, nameof(EquipmentTypeMedia.EquipmentTypeId));
        Assert.True(thumbnail.IsUnique);
        Assert.Equal("\"IsThumbnail\" = TRUE", thumbnail.GetFilter());

        IIndex pair = SingleIndex(media, nameof(EquipmentTypeMedia.EquipmentTypeId), nameof(EquipmentTypeMedia.MediaId));
        Assert.True(pair.IsUnique);
    }
    [Fact]
    public void EquipmentTypeSpecSchema_ConfiguresConstraintsAndIndexesCorrectly()
    {
        IEntityType schemaEntity = Entity<EquipmentTypeSpecSchema>(BuildModel());

        // Alternate key (EquipmentTypeId, Version)
        Assert.Contains(schemaEntity.GetKeys(), key =>
            key.Properties.Select(p => p.Name).SequenceEqual([nameof(EquipmentTypeSpecSchema.EquipmentTypeId), nameof(EquipmentTypeSpecSchema.Version)]));

        // Check constraint Version > 0
        Assert.Contains(schemaEntity.GetCheckConstraints(), c => c.Name == "CK_EquipmentTypeSpecSchemas_Version_Positive");

        // Partial unique index draft
        IIndex draftIndex = SingleIndex(schemaEntity, nameof(EquipmentTypeSpecSchema.EquipmentTypeId));
        Assert.True(draftIndex.IsUnique);
        Assert.Equal("\"PublishedAt\" IS NULL", draftIndex.GetFilter());

        // Cascade delete from EquipmentType
        IForeignKey parentFk = Assert.Single(schemaEntity.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(EquipmentType));
        Assert.Equal(DeleteBehavior.Cascade, parentFk.DeleteBehavior);
    }

    [Fact]
    public void EquipmentType_ConfiguresCompositeForeignKey_ToCurrentSchema()
    {
        IEntityType equipmentType = Entity<EquipmentType>(BuildModel());

        IForeignKey currentSchemaFk = Assert.Single(
            equipmentType.GetForeignKeys(),
            fk => fk.PrincipalEntityType.ClrType == typeof(EquipmentTypeSpecSchema));

        Assert.Equal(DeleteBehavior.Restrict, currentSchemaFk.DeleteBehavior);
        Assert.False(currentSchemaFk.IsRequired);
        Assert.Equal(
            ["Id", "CurrentSchemaVersion"],
            currentSchemaFk.Properties.Select(p => p.Name));
        Assert.Equal(
            ["EquipmentTypeId", "Version"],
            currentSchemaFk.PrincipalKey.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Model_HasNoPendingChangesAgainstMigrations()
    {
        using CatalogServiceDbContext context = CreateContext();

        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Contains(
            context.GetService<IMigrationsAssembly>().Migrations.Keys,
            id => id.EndsWith("_AddEquipmentTypeSpecSchemaAndDropOrganizationId", StringComparison.Ordinal));
    }

    private static IEntityType Entity<TEntity>(IModel model) => model.FindEntityType(typeof(TEntity))!;

    private static IIndex SingleIndex(IEntityType entity, params string[] propertyNames) =>
        Assert.Single(
            entity.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual(propertyNames));

    private static IModel BuildModel()
    {
        using CatalogServiceDbContext context = CreateContext();
        return context.GetService<IDesignTimeModel>().Model;
    }

    // Npgsql chỉ mở kết nối khi chạy lệnh, nên dựng model không cần database thật.
    private static CatalogServiceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CatalogServiceDbContext>()
            .UseNpgsql("Host=localhost;Database=catalog_model;Username=test;Password=test", npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable(
                    CatalogServiceDbContext.MigrationsHistoryTableName,
                    CatalogServiceDbContext.SchemaName))
            .Options);
}
