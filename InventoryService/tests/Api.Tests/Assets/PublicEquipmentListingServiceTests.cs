using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class PublicEquipmentListingServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakePublicListingRepository : IEquipmentListingRepository
    {
        public readonly Dictionary<Guid, EquipmentListing> Store = new();
        public EquipmentListingSearchCriteria? LastCapturedCriteria { get; private set; }

        public Task<EquipmentListing?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) ? l : null);

        public Task<EquipmentListing?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) && l.OwnerUserId == ownerUserId ? l : null);

        public Task<EquipmentListing?> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) && l.Status == EquipmentListingStatus.Active ? l : null);

        public Task<IReadOnlyList<EquipmentListing>> ListForOwnerAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EquipmentListing>>(Store.Values.Where(l => l.OwnerUserId == ownerUserId).Skip(skip).Take(take).ToList());

        public Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.Values.Count(l => l.OwnerUserId == ownerUserId));

        public Task<int> GetTotalQuantityAsync(Guid listingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsPublicAsync(AssetSearchFilters filters, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<int>>(Store.Values.Where(l => l.Status == EquipmentListingStatus.Active).Select(l => l.SchemaVersion).Distinct().OrderBy(v => v).ToList());

        public Task<PublicEquipmentListingSearchResult> SearchPublicAsync(EquipmentListingSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            LastCapturedCriteria = criteria;
            var matching = Store.Values.Where(l => l.Status == EquipmentListingStatus.Active);

            if (!string.IsNullOrWhiteSpace(criteria.Filters.SearchKeyword))
            {
                matching = matching.Where(l => l.Model.Contains(criteria.Filters.SearchKeyword, StringComparison.OrdinalIgnoreCase));
            }

            var list = matching.ToList();
            var paged = list.Skip(criteria.Skip).Take(criteria.Take).Select(l => new PublicEquipmentListingItem(l, 5, 3)).ToList();
            return Task.FromResult(new PublicEquipmentListingSearchResult(paged, list.Count));
        }

        public Task<EquipmentListing?> GetCalendarSnapshotAsync(Guid listingId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(listingId, out var l) ? l : null);

        public void Add(EquipmentListing listing) => Store[listing.Id] = listing;
        public void Update(EquipmentListing listing) => Store[listing.Id] = listing;
    }

    private sealed class FakeCatalogClient : ICatalogServiceClient
    {
        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(id, "CAM", "Camera", null, 1, DateTime.UtcNow, null, DateTime.UtcNow)));

        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogBrandResponse(id, "SONY", "Sony", true)));

        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<CatalogBatchSchemasResponse>> GetEquipmentTypeSchemasBatchAsync(Guid equipmentTypeId, IReadOnlyList<int> versions, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(equipmentTypeId, 1, [])));
    }

    private readonly FakePublicListingRepository _repo = new();
    private readonly FakeCatalogClient _catalogClient = new();
    private readonly TestTimeProvider _clock = new(FixedNow);

    private PublicEquipmentListingService CreateService()
    {
        var mediaOptions = Options.Create(new MediaServiceOptions { PublicGatewayUrl = "https://gateway.example" });
        var queryValidator = new AssetSearchQueryValidator(_clock);
        var filterResolver = new AssetSearchFilterResolver(_catalogClient);

        return new PublicEquipmentListingService(
            _repo,
            queryValidator,
            filterResolver,
            mediaOptions,
            _clock);
    }

    [Fact]
    public async Task GetById_EmptyId_Returns404()
    {
        var service = CreateService();
        var result = await service.GetPublicListingByIdAsync(Guid.Empty);

        Assert.True(result.IsFailure);
        Assert.Equal("LISTING_NOT_FOUND", result.Error.Code);
    }

    [Fact]
    public async Task GetById_NotFound_Returns404()
    {
        var service = CreateService();
        var result = await service.GetPublicListingByIdAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("LISTING_NOT_FOUND", result.Error.Code);
    }

    [Fact]
    public async Task GetById_Draft_Returns404()
    {
        var service = CreateService();
        var id = Guid.NewGuid();
        var listing = EquipmentListing.Create(
            id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "Model Draft", 1, "{}", 100_000m, null, null, "VND", FixedNow).Value;
        _repo.Add(listing);

        var result = await service.GetPublicListingByIdAsync(id);

        Assert.True(result.IsFailure);
        Assert.Equal("LISTING_NOT_FOUND", result.Error.Code);
    }

    [Fact]
    public async Task GetById_Active_ReturnsSuccessWithQuantities()
    {
        var service = CreateService();
        var id = Guid.NewGuid();
        var listing = EquipmentListing.Create(
            id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "Model Active", 1, "{\"spec\":\"val\"}", 100_000m, null, null, "VND", FixedNow).Value;
        listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        var asset1 = InventoryAsset.Create(Guid.NewGuid(), id, "SN-PUB-1", FixedNow).Value;
        listing.Assets.Add(asset1);
        _repo.Add(listing);

        var result = await service.GetPublicListingByIdAsync(id);

        Assert.True(result.IsSuccess);
        var response = result.Value;
        Assert.Equal(id, response.Id);
        Assert.Equal("Model Active", response.Model);
        Assert.Equal(1, response.TotalQuantity);
        Assert.Equal(1, response.AvailableQuantity);
    }

    [Fact]
    public async Task List_RequestedQuantityWithoutDates_Returns400()
    {
        var service = CreateService();
        var request = new ListPublicEquipmentListingsRequest { RequestedQuantity = 2 };

        var result = await service.ListPublicListingsAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_QUERY_PARAMETER", result.Error.Code);
    }
}
