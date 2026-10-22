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
    public async Task Ranking_LexicographicOrdering_InterestBeforePopularity_AndTieBreakers()
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

        var criteria = new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(BrandId: brandId, Sort: AssetSortOption.Recommended),
            personalScores: personalScores,
            globalScores: globalScores,
            skip: 0,
            take: 10);

        var result = await repo.SearchPublicAsync(criteria);

        Assert.Equal("personalized", result.EffectiveTier);
        var resultIds = result.Items.Select(i => i.Listing.Id).ToList();

        // Must not contain inactive listing
        Assert.DoesNotContain(listingInactive.Id, resultIds);

        // Within high interest (typeHigh = 20): listing2 (pop 50) before listing1 (pop 5)
        // High interest (listing2, listing1) strictly before low interest (listing3, interest 10, despite pop 500)
        int idxListing2 = resultIds.IndexOf(listing2.Id);
        int idxListing1 = resultIds.IndexOf(listing1.Id);
        int idxListing3 = resultIds.IndexOf(listing3.Id);

        Assert.True(idxListing2 >= 0 && idxListing1 >= 0 && idxListing3 >= 0);
        Assert.True(idxListing2 < idxListing1, "Listing 2 (higher popularity) must precede Listing 1");
        Assert.True(idxListing1 < idxListing3, "High interest must strictly precede low interest regardless of popularity");
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
        Assert.Equal(popularListing.Id, resultGlobal.Items[0].Listing.Id);

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
}
