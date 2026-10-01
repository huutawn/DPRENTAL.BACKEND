using System.Diagnostics.CodeAnalysis;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.DTOs.Equipments;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
[Collection(PostgreSqlTestGroup.Name)]
public sealed class PostgreSqlIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly CatalogServiceDbContext dbContext;
    private readonly CatalogRepository repository;

    public PostgreSqlIntegrationTests()
    {
        var options = new DbContextOptionsBuilder<CatalogServiceDbContext>()
            .UseNpgsql(PostgreSqlFixture.ConnectionString)
            .Options;

        dbContext = new CatalogServiceDbContext(options);
        repository = new CatalogRepository(dbContext);
    }

    public async Task InitializeAsync()
    {
        await dbContext.Database.MigrateAsync();
        await dbContext.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"catalog\".\"EquipmentMedia\", \"catalog\".\"Equipments\", \"catalog\".\"Categories\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        dbContext.Dispose();
    }
    [Fact]
    public async Task Search_FiltersAndCalculatesTotalCountBeforePaging()
    {
        Category catA = await CreateCategoryAsync("Cat A");
        Category catB = await CreateCategoryAsync("Cat B");
        Guid region1 = Guid.NewGuid();
        Guid region2 = Guid.NewGuid();

        for (int i = 1; i <= 5; i++)
        {
            await CreateEquipmentAsync(
                $"Equip A {i}",
                "Sony",
                100_000m * i,
                EquipmentStatus.Published,
                catA.Id,
                region1);
        }

        await CreateEquipmentAsync("Equip B 1", "Canon", 150_000m, EquipmentStatus.Published, catB.Id, region2);

        var request = new GetEquipmentsRequest
        {
            CategoryId = catA.Id,
            RegionId = region1,
            MinPrice = 150_000m,
            MaxPrice = 450_000m,
            Page = 1,
            PageSize = 2
        };

        PagedResult<EquipmentListItemDto> result = await repository.SearchPublishedAsync(request);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.TotalPages);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item =>
        {
            Assert.True(item.DailyPrice is >= 150_000m and <= 450_000m);
            Assert.Equal(region1, item.RegionId);
            Assert.Equal(catA.Id, item.Category.Id);
        });
    }

    [Fact]
    public async Task Search_ILikeKeyword_MatchesCaseInsensitive_Name_Brand_AndCode()
    {
        Category cat = await CreateCategoryAsync("Cameras");
        Guid region = Guid.NewGuid();

        await CreateEquipmentAsync("SONY Alpha A7 IV", "Sony", 500_000m, EquipmentStatus.Published, cat.Id, region, code: "CAM-SONY-001");
        await CreateEquipmentAsync("Canon EOS R5", "CANON", 600_000m, EquipmentStatus.Published, cat.Id, region, code: "CAM-CANON-002");
        await CreateEquipmentAsync("Blackmagic Pocket 6K", "Blackmagic", 400_000m, EquipmentStatus.Published, cat.Id, region, code: "CAM-BMPCC-003");

        var res1 = await repository.SearchPublishedAsync(new GetEquipmentsRequest { Keyword = "alpha" });
        Assert.Single(res1.Items);
        Assert.Equal("SONY Alpha A7 IV", res1.Items[0].Name);

        var res2 = await repository.SearchPublishedAsync(new GetEquipmentsRequest { Keyword = "canon" });
        Assert.Single(res2.Items);
        Assert.Equal("Canon EOS R5", res2.Items[0].Name);

        var res3 = await repository.SearchPublishedAsync(new GetEquipmentsRequest { Keyword = "bmpcc" });
        Assert.Single(res3.Items);
        Assert.Equal("Blackmagic Pocket 6K", res3.Items[0].Name);
    }

    [Fact]
    public async Task Search_SortsStably_ByPriceAsc_PriceDesc_AndDefault()
    {
        Category cat = await CreateCategoryAsync("Lenses");
        Guid region = Guid.NewGuid();

        var eq1 = await CreateEquipmentAsync("Lens Low", "Sony", 100_000m, EquipmentStatus.Published, cat.Id, region);
        var eq2 = await CreateEquipmentAsync("Lens Mid", "Sony", 200_000m, EquipmentStatus.Published, cat.Id, region);
        var eq3 = await CreateEquipmentAsync("Lens High", "Sony", 300_000m, EquipmentStatus.Published, cat.Id, region);

        var ascResult = await repository.SearchPublishedAsync(new GetEquipmentsRequest { Sort = "price_asc" });
        Assert.Equal(eq1.Id, ascResult.Items[0].Id);
        Assert.Equal(eq2.Id, ascResult.Items[1].Id);
        Assert.Equal(eq3.Id, ascResult.Items[2].Id);

        var descResult = await repository.SearchPublishedAsync(new GetEquipmentsRequest { Sort = "price_desc" });
        Assert.Equal(eq3.Id, descResult.Items[0].Id);
        Assert.Equal(eq2.Id, descResult.Items[1].Id);
        Assert.Equal(eq1.Id, descResult.Items[2].Id);
    }

    [Fact]
    public async Task SearchAndDetail_PicksThumbnailMedia_AndOrdersDetailMedia()
    {
        Category cat = await CreateCategoryAsync("Audio");
        Guid region = Guid.NewGuid();

        Equipment eq = await CreateEquipmentAsync("Mic Wireless", "Rode", 150_000m, EquipmentStatus.Published, cat.Id, region);

        Guid mediaNormal = Guid.NewGuid();
        Guid mediaThumb = Guid.NewGuid();

        dbContext.Set<EquipmentMedia>().AddRange(
            new EquipmentMedia { Id = Guid.NewGuid(), EquipmentId = eq.Id, MediaId = mediaNormal, IsThumbnail = false, DisplayOrder = 0 },
            new EquipmentMedia { Id = Guid.NewGuid(), EquipmentId = eq.Id, MediaId = mediaThumb, IsThumbnail = true, DisplayOrder = 1 }
        );
        await dbContext.SaveChangesAsync();

        var searchResult = await repository.SearchPublishedAsync(new GetEquipmentsRequest { Keyword = "Rode" });
        Assert.Single(searchResult.Items);
        Assert.Equal(mediaThumb, searchResult.Items[0].ThumbnailMediaId);

        EquipmentDetailDto? detail = await repository.GetPublishedByIdAsync(eq.Id);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.MediaIds.Count);
        Assert.Equal(mediaNormal, detail.MediaIds[0]);
        Assert.Equal(mediaThumb, detail.MediaIds[1]);
    }

    [Fact]
    public async Task Detail_MaterializesJsonSpecifications_ToJsonElement()
    {
        Category cat = await CreateCategoryAsync("Lighting");
        Guid region = Guid.NewGuid();

        const string specsJson = """{"sensor":"FullFrame","mount":"E-Mount","weight":640}""";
        Equipment eq = await CreateEquipmentAsync("Aputure 300d", "Aputure", 250_000m, EquipmentStatus.Published, cat.Id, region, specifications: specsJson);

        EquipmentDetailDto? detail = await repository.GetPublishedByIdAsync(eq.Id);

        Assert.NotNull(detail);
        Assert.NotNull(detail.Specifications);
        Assert.Equal("FullFrame", detail.Specifications.Value.GetProperty("sensor").GetString());
        Assert.Equal(640, detail.Specifications.Value.GetProperty("weight").GetInt32());
    }

    [Fact]
    public async Task SearchAndDetail_NeverExposesDraftOrHiddenEquipments()
    {
        Category cat = await CreateCategoryAsync("Drones");
        Guid region = Guid.NewGuid();

        Equipment draft = await CreateEquipmentAsync("Drone Draft", "DJI", 500_000m, EquipmentStatus.Draft, cat.Id, region);
        Equipment hidden = await CreateEquipmentAsync("Drone Hidden", "DJI", 600_000m, EquipmentStatus.Hidden, cat.Id, region);
        Equipment published = await CreateEquipmentAsync("Drone Pub", "DJI", 700_000m, EquipmentStatus.Published, cat.Id, region);

        var result = await repository.SearchPublishedAsync(new GetEquipmentsRequest { Keyword = "DJI" });
        Assert.Single(result.Items);
        Assert.Equal(published.Id, result.Items[0].Id);

        EquipmentDetailDto? detailDraft = await repository.GetPublishedByIdAsync(draft.Id);
        Assert.Null(detailDraft);

        EquipmentDetailDto? detailHidden = await repository.GetPublishedByIdAsync(hidden.Id);
        Assert.Null(detailHidden);

        EquipmentDetailDto? detailPub = await repository.GetPublishedByIdAsync(published.Id);
        Assert.NotNull(detailPub);
        Assert.Equal(published.Id, detailPub.Id);
    }

    private async Task<Category> CreateCategoryAsync(string name)
    {
        Category cat = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTime.UtcNow
        };
        dbContext.Categories.Add(cat);
        await dbContext.SaveChangesAsync();
        return cat;
    }

    private async Task<Equipment> CreateEquipmentAsync(
        string name,
        string brand,
        decimal price,
        EquipmentStatus status,
        Guid categoryId,
        Guid regionId,
        string? code = null,
        string? specifications = null)
    {
        Equipment eq = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(),
            Code = code ?? $"CODE-{Guid.NewGuid():N}"[..12],
            Name = name,
            Brand = brand,
            DailyPrice = price,
            CategoryId = categoryId,
            RegionId = regionId,
            Specifications = specifications ?? Equipment.EmptySpecifications,
            CreatedAtUtc = DateTime.UtcNow
        };

        DateTime now = DateTime.UtcNow;
        if (status == EquipmentStatus.Published)
        {
            eq.Publish(now);
        }
        else if (status == EquipmentStatus.Hidden)
        {
            eq.Publish(now);
            eq.Hide(now.AddSeconds(1));
        }

        dbContext.Equipments.Add(eq);
        await dbContext.SaveChangesAsync();
        return eq;
    }
}
