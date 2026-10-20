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
        public Task<IReadOnlyList<Guid>> GetListingIdsByOwnerKeysetAsync(Guid ownerUserId, Guid? afterListingId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>(Store.Values
                .Where(l => l.OwnerUserId == ownerUserId && (!afterListingId.HasValue || l.Id > afterListingId.Value))
                .OrderBy(l => l.Id)
                .Take(limit)
                .Select(l => l.Id)
                .ToList());
        public Task<IReadOnlyList<EquipmentListingBatchItem>> GetBatchByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
        {
            var list = Store.Values
                .Where(l => ids.Contains(l.Id))
                .Select(l => new EquipmentListingBatchItem(l.Id, l.OwnerUserId, l.Name, l.ThumbnailMediaId, l.DailyRate, l.Currency, l.Status, l.DepositAmount))
                .ToList();
            return Task.FromResult<IReadOnlyList<EquipmentListingBatchItem>>(list);
        }
        public Task<EquipmentListingStatisticsResult> GetListingStatisticsAsync(EquipmentListingStatisticsCriteria criteria, CancellationToken cancellationToken = default)
        {
            var q = Store.Values.AsEnumerable();
            if (criteria.OwnerUserId.HasValue)
            {
                q = q.Where(l => l.OwnerUserId == criteria.OwnerUserId.Value);
            }
            if (criteria.Status.HasValue)
            {
                q = q.Where(l => l.Status == criteria.Status.Value);
            }
            var byType = q.GroupBy(l => l.EquipmentTypeId).ToDictionary(g => g.Key, g => g.Count());
            var byBrand = q.GroupBy(l => l.BrandId).ToDictionary(g => g.Key, g => g.Count());
            return Task.FromResult(new EquipmentListingStatisticsResult(byType, byBrand));
        }

        public Task<OwnerEquipmentListingSearchResult> SearchOwnerAsync(OwnerEquipmentListingSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            var q = Store.Values.Where(l => l.OwnerUserId == criteria.OwnerUserId);
            if (criteria.EquipmentTypeId.HasValue)
            {
                q = q.Where(l => l.EquipmentTypeId == criteria.EquipmentTypeId.Value);
            }
            if (criteria.BrandId.HasValue)
            {
                q = q.Where(l => l.BrandId == criteria.BrandId.Value);
            }
            if (criteria.Status.HasValue)
            {
                q = q.Where(l => l.Status == criteria.Status.Value);
            }
            if (!string.IsNullOrWhiteSpace(criteria.SearchKeyword))
            {
                q = q.Where(l => l.Name.Contains(criteria.SearchKeyword!, StringComparison.OrdinalIgnoreCase) ||
                                 l.Model.Contains(criteria.SearchKeyword!, StringComparison.OrdinalIgnoreCase));
            }
            var list = q.OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id).ToList();
            var items = list.Skip(criteria.Skip).Take(criteria.Take)
                .Select(l => new OwnerEquipmentListingItem(l, l.Assets.Count(a => a.AcceptingBookings)))
                .ToList();
            return Task.FromResult(new OwnerEquipmentListingSearchResult(items, list.Count));
        }

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
                matching = matching.Where(l => l.Name.Contains(criteria.Filters.SearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                                               l.Model.Contains(criteria.Filters.SearchKeyword, StringComparison.OrdinalIgnoreCase));
            }
            if (criteria.Filters.EquipmentTypeId.HasValue)
            {
                matching = matching.Where(l => l.EquipmentTypeId == criteria.Filters.EquipmentTypeId.Value);
            }
            else if (criteria.Filters.EquipmentTypeIds != null)
            {
                matching = matching.Where(l => criteria.Filters.EquipmentTypeIds.Contains(l.EquipmentTypeId));
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

        public Task<Result<Dprental.InventoryService.Application.Caching.CachedRead<CatalogBatchSchemasResponse>>> GetEquipmentTypeSchemasBatchAsync(Guid equipmentTypeId, IReadOnlyList<int> versions, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<CatalogBatchSchemasResponse>(new CatalogBatchSchemasResponse(equipmentTypeId, 1, []), DateTimeOffset.UtcNow.AddMinutes(5))));

        public Func<Guid, Task<Result<IReadOnlyList<Guid>>>>? GetByCategoryHandler { get; set; }

        public async Task<Result<Dprental.InventoryService.Application.Caching.CachedRead<IReadOnlyList<Guid>>>> GetEquipmentTypeIdsByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
        {
            if (GetByCategoryHandler != null)
            {
                var r = await GetByCategoryHandler(categoryId);
                if (r.IsFailure) return r.Error;
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<IReadOnlyList<Guid>>(r.Value, DateTimeOffset.UtcNow.AddMinutes(5)));
            }
            return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<IReadOnlyList<Guid>>(Array.Empty<Guid>(), DateTimeOffset.UtcNow.AddMinutes(5)));
        }
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
            _clock,
            catalogClient: _catalogClient);
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
            "Model Draft", null, "Model Draft", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
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
            "Model Active", null, "Model Active", 1, "{\"spec\":\"val\"}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
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

    [Fact]
    public async Task GetStatistics_ReturnsActiveListingsOnly()
    {
        var typeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        var draftListing = EquipmentListing.Create(
            Guid.NewGuid(), Guid.NewGuid(), typeId, brandId, "Draft", null, "Draft", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        _repo.Add(draftListing);

        var activeListing = EquipmentListing.Create(
            Guid.NewGuid(), Guid.NewGuid(), typeId, brandId, "Active", null, "Active", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        activeListing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        _repo.Add(activeListing);

        var service = CreateService();
        var result = await service.GetStatisticsAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.ByEquipmentType[typeId]);
        Assert.Equal(1, result.Value.ByBrand[brandId]);
    }

    [Fact]
    public async Task List_CategoryAndTypeFilter_WhenTypeNotInCate_FallsBackToCategory()
    {
        var categoryId = Guid.NewGuid();
        var typeInCate = Guid.NewGuid();
        var typeNotInCate = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        _catalogClient.GetByCategoryHandler = _ => Task.FromResult(Result.Success<IReadOnlyList<Guid>>([typeInCate]));

        var listingInCate = EquipmentListing.Create(
            Guid.NewGuid(), Guid.NewGuid(), typeInCate, brandId, "In Cate", null, "In Cate", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        listingInCate.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        _repo.Add(listingInCate);

        var service = CreateService();

        // Request categoryId + typeNotInCate -> falls back to typeInCate from categoryId
        var request = new ListPublicEquipmentListingsRequest(
            CategoryId: categoryId,
            EquipmentTypeId: typeNotInCate);

        var result = await service.ListPublicListingsAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.Value.TotalCount);
        Assert.Equal(listingInCate.Id, result.Value.Value.Items[0].Id);
    }

    [Fact]
    public async Task List_CategoryAndTypeFilter_WhenTypeInCate_FiltersByType()
    {
        var categoryId = Guid.NewGuid();
        var typeA = Guid.NewGuid();
        var typeB = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        _catalogClient.GetByCategoryHandler = _ => Task.FromResult(Result.Success<IReadOnlyList<Guid>>([typeA, typeB]));

        var listingA = EquipmentListing.Create(
            Guid.NewGuid(), Guid.NewGuid(), typeA, brandId, "Type A", null, "Type A", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        listingA.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        _repo.Add(listingA);

        var listingB = EquipmentListing.Create(
            Guid.NewGuid(), Guid.NewGuid(), typeB, brandId, "Type B", null, "Type B", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        listingB.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        _repo.Add(listingB);

        var service = CreateService();

        // Request categoryId + typeA -> filters only typeA
        var request = new ListPublicEquipmentListingsRequest(
            CategoryId: categoryId,
            EquipmentTypeId: typeA);

        var result = await service.ListPublicListingsAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.Value.TotalCount);
        Assert.Equal(listingA.Id, result.Value.Value.Items[0].Id);
    }

    [Fact]
    public async Task List_CategoryOnly_FiltersAllTypesInCategory()
    {
        var categoryId = Guid.NewGuid();
        var typeA = Guid.NewGuid();
        var typeB = Guid.NewGuid();
        var typeC = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        _catalogClient.GetByCategoryHandler = _ => Task.FromResult(Result.Success<IReadOnlyList<Guid>>([typeA, typeB]));

        var listingA = EquipmentListing.Create(
            Guid.NewGuid(), Guid.NewGuid(), typeA, brandId, "Type A", null, "Type A", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        listingA.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        _repo.Add(listingA);

        var listingB = EquipmentListing.Create(
            Guid.NewGuid(), Guid.NewGuid(), typeB, brandId, "Type B", null, "Type B", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        listingB.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        _repo.Add(listingB);

        var listingC = EquipmentListing.Create(
            Guid.NewGuid(), Guid.NewGuid(), typeC, brandId, "Type C", null, "Type C", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        listingC.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        _repo.Add(listingC);

        var service = CreateService();

        var request = new ListPublicEquipmentListingsRequest(
            CategoryId: categoryId);

        var result = await service.ListPublicListingsAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Value.TotalCount);
    }

    [Fact]
    public async Task List_EmptyCategory_WithInvalidInputs_StillValidatesAndReturns400()
    {
        var categoryId = Guid.NewGuid();
        _catalogClient.GetByCategoryHandler = _ => Task.FromResult(Result.Success<IReadOnlyList<Guid>>(Array.Empty<Guid>()));

        var service = CreateService();

        // 1. Invalid date window: from > to
        var requestInvalidDates = new ListPublicEquipmentListingsRequest(
            CategoryId: categoryId,
            From: new DateOnly(2026, 10, 20),
            To: new DateOnly(2026, 10, 10));

        var resultDates = await service.ListPublicListingsAsync(requestInvalidDates);
        Assert.True(resultDates.IsFailure);
        Assert.Equal("INVALID_DATE_ORDER", resultDates.Error.Code);

        // 2. Invalid requestedQuantity: < 1
        var requestInvalidQty = new ListPublicEquipmentListingsRequest(
            CategoryId: categoryId,
            From: new DateOnly(2026, 10, 10),
            To: new DateOnly(2026, 10, 15),
            RequestedQuantity: 0);

        var resultQty = await service.ListPublicListingsAsync(requestInvalidQty);
        Assert.True(resultQty.IsFailure);
        Assert.Equal("INVALID_QUERY_PARAMETER", resultQty.Error.Code);
    }

    [Fact]
    public async Task List_InvalidProvinceCode_ReturnsInvalidLocation()
    {
        var service = CreateService();
        var req = new ListPublicEquipmentListingsRequest(ProvinceCode: "INVALID_PROVINCE_999");

        var result = await service.ListPublicListingsAsync(req);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_LOCATION", result.Error.Code);
    }

    [Fact]
    public async Task List_MismatchedProvinceAndWard_ReturnsInvalidLocation()
    {
        var service = CreateService();
        // Province 79 is HCM, ward 00070 is Hoan Kiem in Ha Noi
        var req = new ListPublicEquipmentListingsRequest(ProvinceCode: "79", WardCode: "00070");

        var result = await service.ListPublicListingsAsync(req);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_LOCATION", result.Error.Code);
    }

    [Fact]
    public async Task List_WardWithoutProvince_ReturnsInvalidLocation()
    {
        var service = CreateService();
        var req = new ListPublicEquipmentListingsRequest(WardCode: "00070");

        var result = await service.ListPublicListingsAsync(req);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_LOCATION", result.Error.Code);
    }

    [Fact]
    public async Task List_ValidProvinceAndWard_PassesCriteriaToRepository()
    {
        var service = CreateService();
        var req = new ListPublicEquipmentListingsRequest(ProvinceCode: "01", WardCode: "00070");

        var result = await service.ListPublicListingsAsync(req);

        Assert.True(result.IsSuccess);
        Assert.NotNull(_repo.LastCapturedCriteria);
        Assert.Equal("01", _repo.LastCapturedCriteria.Filters.ProvinceCode);
        Assert.Equal("00070", _repo.LastCapturedCriteria.Filters.WardCode);
    }

    [Fact]
    public async Task GetById_ExposesWarehouseAreaWithoutSensitiveCoordinatesOrAddress()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var listing = EquipmentListing.Create(
            listingId, ownerId, Guid.NewGuid(), Guid.NewGuid(), "FX3", "Desc", "FX3", 1, "{}", 500_000m, null, null, "VND", FixedNow, 2024).Value;
        var warehouse = Warehouse.CreateWithAddress(
            ownerId, "123 Secret Alley", "01", "Thành phố Hà Nội", "00070", "Phường Hoàn Kiếm", 21.025, 105.856, FixedNow).Value;
        typeof(EquipmentListing).GetProperty(nameof(EquipmentListing.Warehouse))!.SetValue(listing, warehouse);
        typeof(EquipmentListing).GetProperty(nameof(EquipmentListing.Status))!.SetValue(listing, EquipmentListingStatus.Active);
        _repo.Store[listingId] = listing;

        var result = await service.GetPublicListingByIdAsync(listingId);

        Assert.True(result.IsSuccess);
        var response = result.Value;
        Assert.NotNull(response.WarehouseArea);
        Assert.Equal("01", response.WarehouseArea.ProvinceCode);
        Assert.Equal("Thành phố Hà Nội", response.WarehouseArea.ProvinceName);
        Assert.Equal("00070", response.WarehouseArea.WardCode);
        Assert.Equal("Phường Hoàn Kiếm", response.WarehouseArea.WardName);

        // Verify serialization does not contain secret fields
        var json = System.Text.Json.JsonSerializer.Serialize(response);
        Assert.DoesNotContain("Secret Alley", json);
        Assert.DoesNotContain("21.025", json);
        Assert.DoesNotContain("105.856", json);
        Assert.DoesNotContain("latitude", json);
        Assert.DoesNotContain("longitude", json);
        Assert.DoesNotContain("addressDetail", json);
    }
}
