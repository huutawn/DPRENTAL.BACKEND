using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class PublicInventoryAssetServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakePublicAssetRepository : IInventoryAssetRepository
    {
        public readonly Dictionary<Guid, InventoryAsset> Store = new();

        public Task<InventoryAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var a) ? a : null);

        public Task<InventoryAsset?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var a) && a.OwnerUserId == ownerUserId ? a : null);

        public Task<IReadOnlyList<InventoryAsset>> SearchForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> CountForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<InventoryAsset?> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var a) && a.AcceptingBookings ? a : null);

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsPublicAsync(AssetSearchFilters filters, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<int>>(Store.Values.Where(a => a.AcceptingBookings).Select(a => a.SchemaVersion).Distinct().OrderBy(v => v).ToList());
        public PublicAssetSearchCriteria? LastCapturedCriteria { get; private set; }

        public Task<PublicAssetSearchResult> SearchPublicAsync(PublicAssetSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            LastCapturedCriteria = criteria;
            var matching = Store.Values.Where(a => a.AcceptingBookings);

            if (!string.IsNullOrWhiteSpace(criteria.Filters.SearchKeyword))
            {
                matching = matching.Where(a => a.Model.Contains(criteria.Filters.SearchKeyword, StringComparison.OrdinalIgnoreCase));
            }

            var list = matching.ToList();
            var paged = list.Skip(criteria.Skip).Take(criteria.Take).ToList();
            return Task.FromResult(new PublicAssetSearchResult(paged, list.Count));
        }

        public Task<InventoryAsset?> GetCalendarSnapshotAsync(Guid assetId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<InventoryAsset>> GetAvailabilitySnapshotsAsync(IReadOnlyList<Guid> assetIds, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public void Add(InventoryAsset asset) => Store[asset.Id] = asset;
        public void Update(InventoryAsset asset) => Store[asset.Id] = asset;
    }

    private sealed class StubCatalogClient : ICatalogServiceClient
    {
        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid brandId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Func<Guid, IReadOnlyList<int>, Task<Result<CatalogBatchSchemasResponse>>>? BatchSchemasHandler { get; set; }
        public Task<Result<CatalogBatchSchemasResponse>> GetEquipmentTypeSchemasBatchAsync(Guid equipmentTypeId, IReadOnlyList<int> versions, CancellationToken cancellationToken = default) =>
            BatchSchemasHandler != null
                ? BatchSchemasHandler(equipmentTypeId, versions)
                : Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(equipmentTypeId, 1, [new CatalogBatchSchemaItemResponse(1, [])])));
    }

    private readonly FakePublicAssetRepository _repo = new();
    private readonly StubCatalogClient _catalogClient = new();
    private readonly TestTimeProvider _clock = new(FixedNow);
    private readonly PublicInventoryAssetService _service;

    public PublicInventoryAssetServiceTests()
    {
        var mediaOptions = Options.Create(new MediaServiceOptions
        {
            InternalBaseUrl = "http://media:8080",
            PublicGatewayUrl = "https://gateway.example.com",
            ApiKey = "key",
            TimeoutSeconds = 10
        });
        var queryValidator = new AssetSearchQueryValidator(_clock);
        var filterResolver = new AssetSearchFilterResolver(_catalogClient);

        _service = new PublicInventoryAssetService(
            _repo,
            queryValidator,
            filterResolver,
            mediaOptions,
            _clock);
    }

    private static InventoryAsset CreateAsset(Guid id, bool acceptingBookings, string model = "Forklift")
    {
        var rate = AssetRentalRate.Create(id, 100_000m, null, null, "VND", FixedNow).Value;
        var asset = InventoryAsset.Create(
            id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), model, 1, "{}", FixedNow, rate, null, null, null).Value;

        if (!acceptingBookings)
        {
            asset.DisableBookings("Disabled", FixedNow);
        }
        return asset;
    }

    [Fact]
    public async Task GetPublicAssetByIdAsync_EmptyGuid_ReturnsNotFound()
    {
        var result = await _service.GetPublicAssetByIdAsync(Guid.Empty);

        Assert.True(result.IsFailure);
        Assert.Equal("ASSET_NOT_FOUND", result.Error.Code);
    }

    [Fact]
    public async Task GetPublicAssetByIdAsync_NonexistentAsset_ReturnsNotFound()
    {
        var result = await _service.GetPublicAssetByIdAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("ASSET_NOT_FOUND", result.Error.Code);
    }

    [Fact]
    public async Task GetPublicAssetByIdAsync_AssetDisabled_ReturnsIdenticalNotFound()
    {
        var disabledId = Guid.NewGuid();
        var disabledAsset = CreateAsset(disabledId, acceptingBookings: false);
        _repo.Add(disabledAsset);

        var result = await _service.GetPublicAssetByIdAsync(disabledId);

        Assert.True(result.IsFailure);
        Assert.Equal("ASSET_NOT_FOUND", result.Error.Code);
        Assert.Equal("Tài sản không tồn tại.", result.Error.Message);
    }

    [Fact]
    public async Task GetPublicAssetByIdAsync_ValidPublicAsset_ReturnsSuccess()
    {
        var assetId = Guid.NewGuid();
        var asset = CreateAsset(assetId, acceptingBookings: true, model: "Komatsu FD30");
        _repo.Add(asset);

        var result = await _service.GetPublicAssetByIdAsync(assetId);

        Assert.True(result.IsSuccess);
        Assert.Equal(assetId, result.Value.Id);
        Assert.Equal("Komatsu FD30", result.Value.Model);
        Assert.True(result.Value.AcceptingBookings);
    }

    [Fact]
    public async Task ListPublicAssetsAsync_InvalidDateOrder_ReturnsError()
    {
        var from = new DateOnly(2026, 11, 10);
        var to = new DateOnly(2026, 11, 5); // from > to

        var result = await _service.ListPublicAssetsAsync(new ListPublicAssetsRequest(From: from, To: to));

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_DATE_ORDER", result.Error.Code);
    }

    [Fact]
    public async Task ListPublicAssetsAsync_DateWindowExceeds6Months_ReturnsError()
    {
        var from = new DateOnly(2026, 1, 1);
        var to = new DateOnly(2026, 8, 1); // > 6 months

        var result = await _service.ListPublicAssetsAsync(new ListPublicAssetsRequest(From: from, To: to));

        Assert.True(result.IsFailure);
        Assert.Equal("EXCEEDED_HORIZON_LIMIT", result.Error.Code);
    }

    [Fact]
    public async Task ListPublicAssetsAsync_ValidQuery_ReturnsPagedResult()
    {
        var a1 = CreateAsset(Guid.NewGuid(), true, "Forklift A");
        var a2 = CreateAsset(Guid.NewGuid(), true, "Forklift B");
        var a3 = CreateAsset(Guid.NewGuid(), false, "Forklift Disabled");
        _repo.Add(a1);
        _repo.Add(a2);
        _repo.Add(a3);

        var result = await _service.ListPublicAssetsAsync(new ListPublicAssetsRequest(Page: 1, PageSize: 10));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.TotalCount); // Only 2 public
        Assert.Equal(2, result.Value.Items.Count);
        Assert.Equal(1, result.Value.TotalPages);
    }

    [Fact]
    public async Task ListPublicAssetsAsync_CapturesEvaluationTimestamp_AfterCatalogResolution()
    {
        var eqTypeId = Guid.NewGuid();
        _catalogClient.BatchSchemasHandler = (id, versions) =>
        {
            // Clock advances by 10 minutes while waiting for Catalog
            _clock.UtcNow = FixedNow.AddMinutes(10);
            return Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
                id, 1, [new CatalogBatchSchemaItemResponse(1, [
                    new CatalogBatchFieldResponse("tonnage", "Tonnage", "number", true, true, false, 0, 0, 100, null, "tons", null)
                ])])));
        };

        var from = new DateOnly(2026, 11, 1);
        var to = new DateOnly(2026, 11, 10);
        var filterJson = "[{\"field\":\"tonnage\",\"operator\":\"eq\",\"value\":10}]";

        var result = await _service.ListPublicAssetsAsync(new ListPublicAssetsRequest(
            EquipmentTypeId: eqTypeId,
            Filters: filterJson,
            From: from,
            To: to));

        Assert.True(result.IsSuccess);
        Assert.NotNull(_repo.LastCapturedCriteria);
        // Timestamp passed to repository MUST be the one after Catalog resolution!
        Assert.Equal(FixedNow.AddMinutes(10), _repo.LastCapturedCriteria.AvailabilityEvaluationUtc);
    }
}
