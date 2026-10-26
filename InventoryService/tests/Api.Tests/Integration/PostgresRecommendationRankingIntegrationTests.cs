using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class PostgresRecommendationRankingIntegrationTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _fixture;

    public PostgresRecommendationRankingIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    private static async Task SeedWarehouseAsync(InventoryServiceDbContext db, Guid ownerUserId)
    {
        var exists = await db.Warehouses.AnyAsync(w => w.OwnerUserId == ownerUserId);
        if (!exists)
        {
            var wh = Warehouse.CreateDraft(ownerUserId, FixedNow);
            db.Warehouses.Add(wh);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Ranking_WeightedSelection_StablePerSeed_And_ScoreAdvantage()
    {
        await using var db = CreateDbContext();
        var repo = new EquipmentListingRepository(db);

        var ownerA = Guid.NewGuid();
        await SeedWarehouseAsync(db, ownerA);

        var typeHigh = Guid.NewGuid();
        var typeLow = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        // 1. High interest, low popularity
        var listing1 = EquipmentListing.Create(
            Guid.NewGuid(), ownerA, typeHigh, brandId, "Listing 1", null, "Model 1", 1, "{}", 100_000m, null, null, "VND", FixedNow.AddMinutes(1), 2024).Value;
        listing1.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow.AddMinutes(1));

        // 2. High interest, high popularity
        var listing2 = EquipmentListing.Create(
            Guid.NewGuid(), ownerA, typeHigh, brandId, "Listing 2", null, "Model 2", 1, "{}", 100_000m, null, null, "VND", FixedNow.AddMinutes(2), 2024).Value;
        listing2.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow.AddMinutes(2));

        // 3. Low interest, super high popularity
        var listing3 = EquipmentListing.Create(
            Guid.NewGuid(), ownerA, typeLow, brandId, "Listing 3", null, "Model 3", 1, "{}", 100_000m, null, null, "VND", FixedNow.AddMinutes(3), 2024).Value;
        listing3.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow.AddMinutes(3));

        // 4. Inactive listing (must be excluded)
        var listingInactive = EquipmentListing.Create(
            Guid.NewGuid(), ownerA, typeHigh, brandId, "Listing Inactive", null, "Model Inactive", 1, "{}", 100_000m, null, null, "VND", FixedNow.AddMinutes(4), 2024).Value;

        db.EquipmentListings.AddRange(listing1, listing2, listing3, listingInactive);
        await db.SaveChangesAsync();

        var personalScores = new List<EquipmentTypeInterestScore>
        {
            new(typeHigh, 20),
            new(typeLow, 10)
        };

        var globalScores = new List<ListingPopularityScore>
        {
            new(listing1.Id, 5),
            new(listing2.Id, 50),
            new(listing3.Id, 500)
        };

        string seedA = "test_cycle_100";
        var criteriaA = new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(BrandId: brandId, Sort: AssetSortOption.Recommended),
            personalScores: personalScores,
            globalScores: globalScores,
            skip: 0,
            take: 10,
            seed: seedA);

        var result1 = await repo.SearchPublicAsync(criteriaA);
        var result2 = await repo.SearchPublicAsync(criteriaA);

        Assert.Equal("personalized", result1.EffectiveTier);
        var resultIds1 = result1.Items.Select(i => i.Listing.Id).ToList();
        var resultIds2 = result2.Items.Select(i => i.Listing.Id).ToList();

        // 1. Same seed produces exact same order (stability across requests in same cycle)
        Assert.Equal(resultIds1, resultIds2);

        // 2. Must not contain inactive listing
        Assert.DoesNotContain(listingInactive.Id, resultIds1);

        // 3. Paging across pages with same seed does not duplicate items
        var page1 = await repo.SearchPublicAsync(new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(BrandId: brandId, Sort: AssetSortOption.Recommended),
            personalScores: personalScores,
            globalScores: globalScores,
            skip: 0,
            take: 2,
            seed: seedA));
        var page2 = await repo.SearchPublicAsync(new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(BrandId: brandId, Sort: AssetSortOption.Recommended),
            personalScores: personalScores,
            globalScores: globalScores,
            skip: 2,
            take: 2,
            seed: seedA));

        Assert.Equal(2, page1.Items.Count);
        Assert.Single(page2.Items);
        var page1Ids = page1.Items.Select(i => i.Listing.Id).ToList();
        var page2Ids = page2.Items.Select(i => i.Listing.Id).ToList();
        Assert.Empty(page1Ids.Intersect(page2Ids));

        // 4. Multiple cycles demonstrate order changes across seeds
        var orders = new HashSet<string>();
        for (int c = 1; c <= 20; c++)
        {
            var resCycle = await repo.SearchPublicAsync(new EquipmentListingSearchCriteria(
                filters: new AssetSearchFilters(BrandId: brandId, Sort: AssetSortOption.Recommended),
                personalScores: personalScores,
                globalScores: globalScores,
                skip: 0,
                take: 10,
                seed: $"cycle_{c}"));
            orders.Add(string.Join(",", resCycle.Items.Select(i => i.Listing.Id)));
        }
        Assert.True(orders.Count > 1, "Different cycles/seeds must produce different orderings.");
    }

    [Fact]
    public async Task OwnerExclusion_ExcludesOwnerListings_AtAllTiers()
    {
        await using var db = CreateDbContext();
        var repo = new EquipmentListingRepository(db);

        var myUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        await SeedWarehouseAsync(db, myUserId);
        await SeedWarehouseAsync(db, otherUserId);

        var typeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        // My listing (high interest + high popularity)
        var myListing = EquipmentListing.Create(
            Guid.NewGuid(), myUserId, typeId, brandId, "My Listing", null, "My Model", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        myListing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        // Other person's listing
        var otherListing = EquipmentListing.Create(
            Guid.NewGuid(), otherUserId, typeId, brandId, "Other Listing", null, "Other Model", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        otherListing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        db.EquipmentListings.AddRange(myListing, otherListing);
        await db.SaveChangesAsync();

        var personalScores = new List<EquipmentTypeInterestScore> { new(typeId, 100) };
        var globalScores = new List<ListingPopularityScore> { new(myListing.Id, 1000), new(otherListing.Id, 10) };

        var criteria = new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(BrandId: brandId, Sort: AssetSortOption.Recommended),
            personalScores: personalScores,
            globalScores: globalScores,
            excludedOwnerUserId: myUserId,
            skip: 0,
            take: 10);

        var result = await repo.SearchPublicAsync(criteria);
        var resultIds = result.Items.Select(i => i.Listing.Id).ToList();

        Assert.DoesNotContain(myListing.Id, resultIds);
        Assert.Contains(otherListing.Id, resultIds);
    }

    [Fact]
    public async Task TierFallback_PersonalMiss_FallsBackToGlobal_ThenDefault()
    {
        await using var db = CreateDbContext();
        var repo = new EquipmentListingRepository(db);

        var owner = Guid.NewGuid();
        await SeedWarehouseAsync(db, owner);

        var typeExisting = Guid.NewGuid();
        var typeNonExisting = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        var popularListing = EquipmentListing.Create(
            Guid.NewGuid(), owner, typeExisting, brandId, "Popular Listing", null, "Model Pop", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        popularListing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        var regularListing = EquipmentListing.Create(
            Guid.NewGuid(), owner, typeExisting, brandId, "Regular Listing", null, "Model Reg", 1, "{}", 100_000m, null, null, "VND", FixedNow.AddSeconds(-10), 2024).Value;
        regularListing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow.AddSeconds(-10));

        db.EquipmentListings.AddRange(popularListing, regularListing);
        await db.SaveChangesAsync();

        // 1. Personal interest has only typeNonExisting -> personal match count = 0 -> falls back to global tier!
        var personalScores = new List<EquipmentTypeInterestScore> { new(typeNonExisting, 50) };
        var globalScores = new List<ListingPopularityScore> { new(popularListing.Id, 200) };

        var criteriaGlobalFallback = new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(BrandId: brandId, Sort: AssetSortOption.Recommended),
            personalScores: personalScores,
            globalScores: globalScores,
            skip: 0,
            take: 10);

        var resultGlobal = await repo.SearchPublicAsync(criteriaGlobalFallback);
        Assert.Equal("global", resultGlobal.EffectiveTier);
        Assert.Equal(2, resultGlobal.Items.Count);
        var globalIds = resultGlobal.Items.Select(i => i.Listing.Id).ToList();
        Assert.Contains(popularListing.Id, globalIds);
        Assert.Contains(regularListing.Id, globalIds);

        // 2. Both personal and global have no match -> falls back to default tier!
        var emptyGlobal = new List<ListingPopularityScore> { new(Guid.NewGuid(), 100) };
        var criteriaDefaultFallback = new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(BrandId: brandId, Sort: AssetSortOption.Recommended),
            personalScores: personalScores,
            globalScores: emptyGlobal,
            skip: 0,
            take: 10);

        var resultDefault = await repo.SearchPublicAsync(criteriaDefaultFallback);
        Assert.Equal("default", resultDefault.EffectiveTier);
        // Default tier orders by created_at DESC -> popularListing (FixedNow) before regularListing (FixedNow - 10s)
        Assert.Equal(popularListing.Id, resultDefault.Items[0].Listing.Id);
    }

    [Fact]
    public async Task Pagination_TotalCountIncludesAllValid_PageBeyondTotalReturnsEmpty()
    {
        await using var db = CreateDbContext();
        var repo = new EquipmentListingRepository(db);

        var owner = Guid.NewGuid();
        await SeedWarehouseAsync(db, owner);
        var typeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        var l1 = EquipmentListing.Create(Guid.NewGuid(), owner, typeId, brandId, "L1", null, "M1", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        l1.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        var l2 = EquipmentListing.Create(Guid.NewGuid(), owner, typeId, brandId, "L2", null, "M2", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        l2.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        db.EquipmentListings.AddRange(l1, l2);
        await db.SaveChangesAsync();

        var criteriaPageBeyond = new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(BrandId: brandId, Sort: AssetSortOption.Recommended),
            skip: 100,
            take: 10);

        var resultBeyond = await repo.SearchPublicAsync(criteriaPageBeyond);
        Assert.Equal(2, resultBeyond.TotalCount);
        Assert.Empty(resultBeyond.Items);
    }

    [Fact]
    public async Task Ranking_StatisticalAdvantage_256Seeds_GlobalAndPersonalized()
    {
        await using var db = CreateDbContext();
        var repo = new EquipmentListingRepository(db);

        var owner = Guid.NewGuid();
        await SeedWarehouseAsync(db, owner);
        var brandId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var typeOther = Guid.NewGuid();

        // Two global candidates
        var lHigh = EquipmentListing.Create(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), owner, typeId, brandId, "High", null, "M1", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        lHigh.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        var lLow = EquipmentListing.Create(
            Guid.Parse("22222222-2222-2222-2222-222222222222"), owner, typeOther, brandId, "Low", null, "M2", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        lLow.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        db.EquipmentListings.AddRange(lHigh, lLow);
        await db.SaveChangesAsync();

        // 1. Global: High (pop 100) vs Low (pop 10) across 256 fixed seeds
        int globalHighWins = 0;
        var globalScores = new List<ListingPopularityScore>
        {
            new(lHigh.Id, 100),
            new(lLow.Id, 10)
        };

        for (int i = 0; i < 256; i++)
        {
            var res = await repo.SearchPublicAsync(new EquipmentListingSearchCriteria(
                filters: new AssetSearchFilters(BrandId: brandId, Sort: AssetSortOption.Recommended),
                globalScores: globalScores,
                skip: 0,
                take: 1,
                seed: $"seed_{i}"));

            if (res.Items.Count > 0 && res.Items[0].Listing.Id == lHigh.Id)
            {
                globalHighWins++;
            }
        }

        double globalWinRate = (double)globalHighWins / 256.0;
        Assert.True(globalWinRate > 0.70, $"Global high popularity win rate was {globalWinRate:P1}, expected > 70%");

        // 2. Personalized: High interest (score 50) vs Normal (score 0) across 256 fixed seeds
        int personalHighWins = 0;
        var personalScores = new List<EquipmentTypeInterestScore>
        {
            new(typeId, 50)
        };

        for (int i = 0; i < 256; i++)
        {
            var res = await repo.SearchPublicAsync(new EquipmentListingSearchCriteria(
                filters: new AssetSearchFilters(BrandId: brandId, Sort: AssetSortOption.Recommended),
                personalScores: personalScores,
                skip: 0,
                take: 1,
                seed: $"seed_{i}"));

            if (res.Items.Count > 0 && res.Items[0].Listing.Id == lHigh.Id)
            {
                personalHighWins++;
            }
        }

        double personalWinRate = (double)personalHighWins / 256.0;
        Assert.True(personalWinRate > 0.85, $"Personalized high interest win rate was {personalWinRate:P1}, expected > 85%");
    }
}
