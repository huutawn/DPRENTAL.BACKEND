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
        Assert.Equal("Equipments", Entity<Equipment>(model).GetTableName());
        Assert.Equal("EquipmentMedia", Entity<EquipmentMedia>(model).GetTableName());
    }

    [Fact]
    public void CategorySlug_IsUnique()
    {
        IIndex index = SingleIndex(Entity<Category>(BuildModel()), nameof(Category.Slug));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void EquipmentCode_IsUniquePerOrganization()
    {
        IIndex index = SingleIndex(
            Entity<Equipment>(BuildModel()),
            nameof(Equipment.OrganizationId),
            nameof(Equipment.Code));

        Assert.True(index.IsUnique);
    }

    [Theory]
    [InlineData(nameof(Equipment.Status))]
    [InlineData(nameof(Equipment.CategoryId))]
    [InlineData(nameof(Equipment.RegionId))]
    [InlineData(nameof(Equipment.DailyPrice))]
    [InlineData(nameof(Equipment.CreatedAtUtc))]
    [InlineData(nameof(Equipment.PublishedAtUtc))]
    public void Equipment_HasIndexOn(string propertyName)
    {
        IIndex index = SingleIndex(Entity<Equipment>(BuildModel()), propertyName);

        Assert.False(index.IsUnique);
    }

    [Fact]
    public void EquipmentSpecifications_AreStoredAsJsonb()
    {
        IProperty property = Entity<Equipment>(BuildModel()).FindProperty(nameof(Equipment.Specifications))!;

        Assert.Equal("jsonb", property.GetColumnType());
        Assert.False(property.IsNullable);
    }

    [Fact]
    public void EquipmentStatus_IsStoredAsString()
    {
        IProperty property = Entity<Equipment>(BuildModel()).FindProperty(nameof(Equipment.Status))!;

        Assert.Equal("character varying(20)", property.GetColumnType());
        Assert.Equal(typeof(string), property.GetTypeMapping().Converter?.ProviderClrType);
    }

    [Fact]
    public void EquipmentDailyPrice_CannotBeNegative()
    {
        IEntityType equipment = Entity<Equipment>(BuildModel());

        Assert.Contains(
            equipment.GetCheckConstraints(),
            constraint => constraint.Name == CatalogServiceDbContext.DailyPriceCheckConstraintName);
    }

    [Fact]
    public void EquipmentSpecifications_MustBeJsonObject()
    {
        IEntityType equipment = Entity<Equipment>(BuildModel());

        Assert.Contains(
            equipment.GetCheckConstraints(),
            constraint => constraint.Name == CatalogServiceDbContext.SpecificationsCheckConstraintName);
    }

    [Theory]
    [InlineData(typeof(Category))]
    [InlineData(typeof(Equipment))]
    [InlineData(typeof(EquipmentMedia))]
    public void Ids_AreAssignedByApplication(Type entityType)
    {
        IProperty id = BuildModel().FindEntityType(entityType)!.FindProperty("Id")!;

        Assert.Equal(ValueGenerated.Never, id.ValueGenerated);
    }

    [Fact]
    public void Equipment_ReferencesCategory_WithoutCascadeDelete()
    {
        IForeignKey foreignKey = Assert.Single(Entity<Equipment>(BuildModel()).GetForeignKeys());

        Assert.Equal(typeof(Category), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void EquipmentMedia_BelongsToEquipment_AndIsDeletedWithIt()
    {
        IForeignKey foreignKey = Assert.Single(Entity<EquipmentMedia>(BuildModel()).GetForeignKeys());

        Assert.Equal(typeof(Equipment), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void EquipmentMedia_AllowsOneThumbnailPerEquipment()
    {
        IEntityType media = Entity<EquipmentMedia>(BuildModel());

        IIndex thumbnail = SingleIndex(media, nameof(EquipmentMedia.EquipmentId));
        Assert.True(thumbnail.IsUnique);
        Assert.Equal("\"IsThumbnail\" = TRUE", thumbnail.GetFilter());

        IIndex pair = SingleIndex(media, nameof(EquipmentMedia.EquipmentId), nameof(EquipmentMedia.MediaId));
        Assert.True(pair.IsUnique);
    }

    [Fact]
    public void Model_HasNoPendingChangesAgainstMigrations()
    {
        using CatalogServiceDbContext context = CreateContext();

        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Contains(
            context.GetService<IMigrationsAssembly>().Migrations.Keys,
            id => id.EndsWith("_InitialCatalogSchema", StringComparison.Ordinal));
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
            .UseNpgsql("Host=localhost;Database=catalog_model;Username=test;Password=test")
            .Options);
}
