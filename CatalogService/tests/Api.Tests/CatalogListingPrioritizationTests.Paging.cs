using System.Data.Common;
using System.Net;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed partial class CatalogListingPrioritizationTests
{
    [Theory]
    [InlineData("listing_count_desc", "ABC")]
    [InlineData("listing_count_asc", "BAC")]
    [InlineData("name_asc", "ABC")]
    [InlineData("name_desc", "CBA")]
    [InlineData("newest", "CBA")]
    public async Task Repositories_SortAcrossPages_WithoutMaterializingAllMetadata(string sort, string order)
    {
        var category = new Category { Id = Guid.NewGuid(), Name = "Paging", Slug = "paging", IsActive = true };
        _dbContext.Categories.Add(category);
        await _dbContext.SaveChangesAsync();
        var types = new List<EquipmentType>();
        var brands = new List<Brand>();
        foreach (char name in "ABC")
        {
            types.Add(await CreateAndPersistPublishedEquipmentTypeAsync(Guid.NewGuid(), $"TYPE-{name}", name.ToString(), category));
            brands.Add(new Brand
            {
                Id = Guid.NewGuid(),
                Code = $"BRAND-{name}",
                Name = name.ToString(),
                Slug = $"brand-{name}",
                IsActive = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(name - 'A')
            });
        }
        _dbContext.Brands.AddRange(brands);
        await _dbContext.SaveChangesAsync();
        var typeCounts = new Dictionary<Guid, int> { [types[0].Id] = 5, [types[1].Id] = 2 };
        var brandCounts = new Dictionary<Guid, int> { [brands[0].Id] = 5, [brands[1].Id] = 2 };
        var capture = new QueryCapture();
        var options = new DbContextOptionsBuilder<CatalogServiceDbContext>()
            .UseNpgsql(PostgreSqlFixture.ConnectionString).AddInterceptors(capture).Options;
        await using var db = new CatalogServiceDbContext(options);
        var typeRepository = new EquipmentTypeRepository(db);
        var brandRepository = new BrandRepository(db);
        var typeNames = new List<string>();
        var brandNames = new List<string>();
        for (int page = 1; page <= 3; page++)
        {
            var typePage = await typeRepository.SearchPublishedAsync(new GetEquipmentTypesRequest { Page = page, PageSize = 2, Sort = sort }, typeCounts);
            var brandPage = await brandRepository.SearchAsync(new BrandSearchQuery { Page = page, PageSize = 2, Sort = sort, IsActive = true, ListingCounts = brandCounts });
            Assert.Equal(3, typePage.TotalCount);
            Assert.Equal(3, brandPage.TotalCount);
            Assert.InRange(typePage.Items.Count, 0, 2);
            Assert.InRange(brandPage.Items.Count, 0, 2);
            typeNames.AddRange(typePage.Items.Select(t => t.Name));
            brandNames.AddRange(brandPage.Items.Select(t => t.Name));
        }
        Assert.Equal(order, string.Concat(typeNames));
        Assert.Equal(order, string.Concat(brandNames));
        var rankingQueries = capture.Commands.Where(sql => sql.Contains("unnest(", StringComparison.Ordinal)).ToList();
        Assert.Equal(6, rankingQueries.Count);
        Assert.All(rankingQueries, sql =>
        {
            Assert.Contains("LIMIT", sql, StringComparison.Ordinal);
            Assert.Contains("OFFSET", sql, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Repositories_EscapeLiteralWildcards_AndShareFiltersWithAdmin()
    {
        var category = new Category { Id = Guid.NewGuid(), Name = "Literals", Slug = "literals", IsActive = true };
        _dbContext.Categories.Add(category);
        await _dbContext.SaveChangesAsync();
        var literal = await CreateAndPersistPublishedEquipmentTypeAsync(Guid.NewGuid(), "LITERAL", "Rate 50% _sale_", category);
        await CreateAndPersistPublishedEquipmentTypeAsync(Guid.NewGuid(), "OTHER", "Rate 500 XsaleX", category);
        literal.Description = "Unique description";
        _dbContext.Brands.AddRange(
            new Brand { Id = Guid.NewGuid(), Code = "LITERAL", Name = "Rate 50% _sale_", Slug = "literal", IsActive = true },
            new Brand { Id = Guid.NewGuid(), Code = "OTHER", Name = "Rate 500 XsaleX", Slug = "other", IsActive = true });
        await _dbContext.SaveChangesAsync();
        foreach (string keyword in new[] { "50%", "_sale_", "Unique description" })
        {
            var result = await new EquipmentTypeRepository(_dbContext).SearchPublishedAsync(
                new GetEquipmentTypesRequest { Keyword = keyword }, new Dictionary<Guid, int>());
            Assert.Equal(literal.Id, Assert.Single(result.Items).Id);
        }
        foreach (string keyword in new[] { "50%", "_sale_" })
        {
            var result = await new BrandRepository(_dbContext).SearchAsync(new BrandSearchQuery
            { Keyword = keyword, IsActive = true, ListingCounts = new Dictionary<Guid, int>() });
            Assert.Equal("LITERAL", Assert.Single(result.Items).Code);
        }
    }

    [Theory]
    [InlineData("categories?unsupported=1")]
    [InlineData("brands?unsupported=1")]
    [InlineData("equipment-types?unsupported=1")]
    [InlineData("categories?scope=mine&status=Active")]
    [InlineData("brands?scope=mine&status=Active")]
    [InlineData("equipment-types?scope=mine&status=Active")]
    [InlineData("categories?scope=public&scope=mine")]
    [InlineData("brands?sort=name_asc&sort=name_desc")]
    public async Task Discovery_RejectsUnknownAliasesAndDuplicateScalars(string query)
    {
        using var response = await _client.GetAsync("/api/v1/catalog/" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryCountAndPage_UseSameSnapshotDuringMetadataChange(bool brands)
    {
        var category = new Category { Id = Guid.NewGuid(), Name = "Snapshot", Slug = "snapshot", IsActive = true };
        _dbContext.Categories.Add(category);
        var brand = new Brand { Id = Guid.NewGuid(), Code = "SNAPSHOT", Name = "Snapshot", Slug = "snapshot", IsActive = true };
        _dbContext.Brands.Add(brand);
        await _dbContext.SaveChangesAsync();
        var type = await CreateAndPersistPublishedEquipmentTypeAsync(Guid.NewGuid(), "SNAPSHOT", "Snapshot", category);
        var gate = new CountSnapshotGate(brands ? "Brands" : "EquipmentTypes");
        var options = new DbContextOptionsBuilder<CatalogServiceDbContext>()
            .UseNpgsql(PostgreSqlFixture.ConnectionString).AddInterceptors(gate).Options;
        await using var reader = new CatalogServiceDbContext(options);
        var counts = new Dictionary<Guid, int>();
        if (brands)
        {
            var read = new BrandRepository(reader).SearchAsync(new BrandSearchQuery { IsActive = true, ListingCounts = counts });
            try
            {
                await gate.CountRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
                brand.IsActive = false;
                await _dbContext.SaveChangesAsync();
            }
            finally { gate.WriterCommitted.TrySetResult(); }
            var page = await read;
            Assert.Equal(1, page.TotalCount);
            Assert.Equal(brand.Id, Assert.Single(page.Items).Id);
            Assert.True(page.Items[0].IsActive);
        }
        else
        {
            var read = new EquipmentTypeRepository(reader).SearchPublishedAsync(new GetEquipmentTypesRequest(), counts);
            try
            {
                await gate.CountRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
                type.Disable(DateTime.UtcNow);
                await _dbContext.SaveChangesAsync();
            }
            finally { gate.WriterCommitted.TrySetResult(); }
            var page = await read;
            Assert.Equal(1, page.TotalCount);
            Assert.Equal(type.Id, Assert.Single(page.Items).Id);
        }
    }

    private sealed class CountSnapshotGate(string table) : DbCommandInterceptor
    {
        public TaskCompletionSource CountRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriterCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains(table, StringComparison.Ordinal))
            {
                CountRead.TrySetResult();
                await WriterCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class QueryCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
