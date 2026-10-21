using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class PostgresEquipmentListingContentAndSearchTests
{
    [Fact]
    public async Task PublicSearch_RecommendationRanksAfterCurrentFiltersAndBeforePagination()
    {
        await using var db = CreateDbContext();
        var repository = new EquipmentListingRepository(db);
        var viewerId = Guid.NewGuid();
        var otherOwnerId = Guid.NewGuid();
        var recommendedTypeId = Guid.NewGuid();
        var otherTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        var recommended = await IntegrationTestHelper.InsertTestListingAsync(db,
            ownerId: otherOwnerId, eqTypeId: recommendedTypeId, brandId: brandId,
            name: "Recommended", model: "R1", status: EquipmentListingStatus.Active);
        var ordinary = await IntegrationTestHelper.InsertTestListingAsync(db,
            ownerId: otherOwnerId, eqTypeId: otherTypeId, brandId: brandId,
            name: "Ordinary", model: "O1", status: EquipmentListingStatus.Active);
        var own = await IntegrationTestHelper.InsertTestListingAsync(db,
            ownerId: viewerId, eqTypeId: recommendedTypeId, brandId: brandId,
            name: "Own", model: "R2", status: EquipmentListingStatus.Active);
        var inactive = await IntegrationTestHelper.InsertTestListingAsync(db,
            ownerId: otherOwnerId, eqTypeId: recommendedTypeId, brandId: brandId,
            name: "Inactive", model: "R3", status: EquipmentListingStatus.Inactive);

        var criteria = new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(BrandId: brandId, Sort: AssetSortOption.Recommended),
            skip: 0, take: 1)
        {
            ExcludedOwnerUserId = viewerId,
            Recommendation = new RecommendationPriority(
                RecommendationPriorityKind.PersonalTypes, [recommendedTypeId])
        };

        var firstPage = await repository.SearchPublicAsync(criteria);
        Assert.Equal(2, firstPage.TotalCount);
        Assert.Equal(1, firstPage.PriorityCandidateCount);
        Assert.Equal(recommended.Id, Assert.Single(firstPage.Items).Listing.Id);

        var secondPage = await repository.SearchPublicAsync(criteria with { Skip = 1 });
        Assert.Equal(ordinary.Id, Assert.Single(secondPage.Items).Listing.Id);

        var global = await repository.SearchPublicAsync(criteria with
        {
            Recommendation = new RecommendationPriority(
                RecommendationPriorityKind.GlobalListings, [ordinary.Id, own.Id, inactive.Id])
        });
        Assert.Equal(1, global.PriorityCandidateCount);
        Assert.Equal(ordinary.Id, Assert.Single(global.Items).Listing.Id);
    }
}
