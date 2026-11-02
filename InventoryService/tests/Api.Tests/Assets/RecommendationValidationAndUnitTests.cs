using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Recommendation;
using Dprental.InventoryService.Application.Search;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class RecommendationValidationAndUnitTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeCatalogClient : ICatalogServiceClient
    {
        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(id, "CAM", "Camera", null, 1, DateTime.UtcNow, null, DateTime.UtcNow)));

        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogBrandResponse(id, "SONY", "Sony", true)));

        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<CachedRead<CatalogBatchSchemasResponse>>> GetEquipmentTypeSchemasBatchAsync(Guid equipmentTypeId, IReadOnlyList<int> versions, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CachedRead<CatalogBatchSchemasResponse>(new CatalogBatchSchemasResponse(equipmentTypeId, 1, []), DateTimeOffset.UtcNow.AddMinutes(5))));

        public Task<Result<CachedRead<IReadOnlyList<Guid>>>> GetEquipmentTypeIdsByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CachedRead<IReadOnlyList<Guid>>(Array.Empty<Guid>(), DateTimeOffset.UtcNow.AddMinutes(5))));
    }

    private sealed class FakeListingRepository : IEquipmentListingRepository
    {
        public readonly Dictionary<Guid, EquipmentListing> Store = new();
        public EquipmentListingSearchCriteria? LastCapturedCriteria { get; private set; }

        public void Add(EquipmentListing listing) => Store[listing.Id] = listing;
        public void Update(EquipmentListing listing) => Store[listing.Id] = listing;

        public Task<EquipmentListing?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) ? l : null);

        public Task<EquipmentListing?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) && l.OwnerUserId == ownerUserId ? l : null);

        public Task<EquipmentListing?> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) && l.Status == EquipmentListingStatus.Active ? l : null);

        public Task<EquipmentListing?> GetByIdWithAssetsAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) ? l : null);

        public Task<EquipmentListing?> GetCalendarSnapshotAsync(Guid id, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) ? l : null);

        public Task<int> GetTotalQuantityAsync(Guid listingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsPublicAsync(AssetSearchFilters filters, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<int>>([1]);

        public Task<IReadOnlyList<Guid>> GetListingIdsByOwnerKeysetAsync(Guid ownerUserId, Guid? afterListingId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<EquipmentListingBatchItem>> GetBatchAsync(IReadOnlyList<Guid> listingIds, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<EquipmentListingBatchItem>> GetBatchByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EquipmentListingBatchItem>>([]);

        public Task<PublicEquipmentListingSearchResult> SearchPublicAsync(EquipmentListingSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            LastCapturedCriteria = criteria;
            var items = Store.Values
                .Where(l => l.Status == EquipmentListingStatus.Active)
                .Where(l => !criteria.ExcludedOwnerUserId.HasValue || l.OwnerUserId != criteria.ExcludedOwnerUserId.Value)
                .Select(l => new PublicEquipmentListingItem(l, 1, 1))
                .ToList();

            string effectiveTier = criteria.PersonalScores != null && criteria.PersonalScores.Count > 0 ? "personalized"
                : criteria.GlobalScores != null && criteria.GlobalScores.Count > 0 ? "global"
                : "default";

            return Task.FromResult(new PublicEquipmentListingSearchResult(items, items.Count, null, effectiveTier));
        }

        public Task<OwnerEquipmentListingSearchResult> SearchOwnerAsync(OwnerEquipmentListingSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<EquipmentListingStatisticsResult> GetListingStatisticsAsync(EquipmentListingStatisticsCriteria criteria, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> GetActiveListingCountAsync(Guid ownerUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeRecommendationReader : IRecommendationRankingReader
    {
        public Guid? LastUserId { get; private set; }
        public int CallCount { get; private set; }
        public RecommendationSnapshot ReturnSnapshot { get; set; } = RecommendationSnapshot.Miss();
        public bool ThrowTimeout { get; set; }
        public bool ObserveCancellation { get; set; }

        public Task<RecommendationSnapshot> ReadAsync(Guid? userId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastUserId = userId;

            if (ObserveCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (ThrowTimeout)
            {
                return Task.FromResult(RecommendationSnapshot.Unavailable(RecommendationReasons.Timeout));
            }

            return Task.FromResult(ReturnSnapshot);
        }
    }

    private sealed class SpyReadCache : IInventoryReadCache
    {
        private readonly NoOpInventoryReadCache _inner = NoOpInventoryReadCache.Instance;
        public int GetOrCreateCalls { get; private set; }

        public bool IsEnabled => _inner.IsEnabled;

        public Task<CacheReadResult<T>> GetAsync<T>(string scope, string queryType, string inputHash, CancellationToken cancellationToken = default) =>
            _inner.GetAsync<T>(scope, queryType, inputHash, cancellationToken);

        public Task<bool> SetAsync<T>(string scope, string queryType, string inputHash, T value, string expectedGenerationToken, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default) =>
            _inner.SetAsync<T>(scope, queryType, inputHash, value, expectedGenerationToken, expiresAtUtc, cancellationToken);

        public Task<IReadOnlyDictionary<string, CacheReadResult<T>>> GetBulkAsync<T>(string scope, string queryType, IReadOnlyList<string> inputHashes, CancellationToken cancellationToken = default) =>
            _inner.GetBulkAsync<T>(scope, queryType, inputHashes, cancellationToken);

        public Task SetBulkAsync<T>(string scope, string queryType, IReadOnlyList<(string InputHash, T Value, DateTimeOffset ExpiresAtUtc)> items, string expectedGenerationToken, CancellationToken cancellationToken = default) =>
            _inner.SetBulkAsync<T>(scope, queryType, items, expectedGenerationToken, cancellationToken);

        public Task<Result<T>> GetOrCreateAsync<T>(string scope, string queryType, string inputHash, Func<string, CancellationToken, Task<Result<CachedRead<T>>>> factory, CancellationToken cancellationToken = default)
        {
            GetOrCreateCalls++;
            return _inner.GetOrCreateAsync<T>(scope, queryType, inputHash, factory, cancellationToken);
        }

        public Task<Result<IReadOnlyDictionary<CacheTarget, T>>> GetOrCreateBulkAsync<T>(IReadOnlyList<CacheTarget> targets, Func<IReadOnlyList<CacheTarget>, CancellationToken, Task<Result<IReadOnlyDictionary<CacheTarget, CachedRead<T>>>>> factory, CancellationToken cancellationToken = default) =>
            _inner.GetOrCreateBulkAsync<T>(targets, factory, cancellationToken);

        public Task InvalidateScopeAsync(string scope, CancellationToken cancellationToken = default) =>
            _inner.InvalidateScopeAsync(scope, cancellationToken);

        public Task InvalidateScopesAsync(IEnumerable<string> scopes, CancellationToken cancellationToken = default) =>
            _inner.InvalidateScopesAsync(scopes, cancellationToken);
    }

    private sealed class HumanTestCaller(Guid userId) : IInventoryCallerContext
    {
        public bool IsAuthenticated => true;
        public bool IsService => false;
        public bool IsHuman => true;
        public bool IsAdmin => false;
        public Guid? UserId => userId;
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> Scopes { get; } = new HashSet<string>();
        public IReadOnlySet<string> Roles { get; } = new HashSet<string>();
        public Guid? OrganizationId => null;
    }

    private sealed class ServiceTestCaller : IInventoryCallerContext
    {
        public bool IsAuthenticated => true;
        public bool IsService => true;
        public bool IsHuman => false;
        public bool IsAdmin => false;
        public Guid? UserId { get; } = Guid.NewGuid();
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> Scopes { get; } = new HashSet<string>();
        public IReadOnlySet<string> Roles { get; } = new HashSet<string>();
        public Guid? OrganizationId => null;
    }

    private static PublicEquipmentListingService CreateService(
        FakeListingRepository repo,
        FakeRecommendationReader? reader = null,
        IInventoryReadCache? cache = null)
    {
        var catalogClient = new FakeCatalogClient();
        return new PublicEquipmentListingService(
            repo,
            new AssetSearchQueryValidator(),
            new AssetSearchFilterResolver(catalogClient),
            Options.Create(new MediaServiceOptions { PublicGatewayUrl = "https://gateway.example" }),
            catalogClient: catalogClient,
            cache: cache,
            recommendationReader: reader);
    }

    // 1. Validation tests
    [Fact]
    public void ValidateListingQuery_AcceptsRecommended()
    {
        var result = AssetSearchQueryValidator.ValidateListingQuery(1, 20, null, null, null, "recommended");
        Assert.True(result.IsSuccess);
        Assert.Equal(AssetSortOption.Recommended, result.Value.Sort);
    }

    [Fact]
    public void AssetSearchQueryValidator_GeneralValidate_RejectsRecommended()
    {
        var validator = new AssetSearchQueryValidator();
        var result = validator.Validate(1, 20, null, null, null, null, null, "recommended");
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_QUERY_PARAMETER", result.Error.Code);
    }

    [Fact]
    public void ValidateListingQuery_RecommendedWithKeyword_NormalizesToRelevance()
    {
        var result = AssetSearchQueryValidator.ValidateListingQuery(1, 20, "   sony fx3   ", null, null, "recommended");
        Assert.True(result.IsSuccess);
        Assert.Equal(AssetSortOption.Relevance, result.Value.Sort);
        Assert.Equal("sony fx3", result.Value.SearchKeyword);
    }

    [Fact]
    public void ValidateListingQuery_RecommendedWithWhitespaceOnlyKeyword_StaysRecommended()
    {
        var result = AssetSearchQueryValidator.ValidateListingQuery(1, 20, "     ", null, null, "recommended");
        Assert.True(result.IsSuccess);
        Assert.Equal(AssetSortOption.Recommended, result.Value.Sort);
        Assert.Null(result.Value.SearchKeyword);
    }

    // 2. Service caller context & routing tests
    [Fact]
    public async Task ListPublicListings_NonRecommendedSort_DoesNotInvokeRankingReader()
    {
        var repo = new FakeListingRepository();
        var reader = new FakeRecommendationReader();
        var cache = new SpyReadCache();
        var service = CreateService(repo, reader, cache);

        var request = new ListPublicEquipmentListingsRequest { Sort = "created_desc" };
        var result = await service.ListPublicListingsAsync(request, AnonymousCallerContext.Instance);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, reader.CallCount);
        Assert.Equal(1, cache.GetOrCreateCalls);
    }

    [Fact]
    public async Task ListPublicListings_Anonymous_CallsReaderWithNullUserId()
    {
        var repo = new FakeListingRepository();
        var reader = new FakeRecommendationReader();
        var cache = new SpyReadCache();
        var service = CreateService(repo, reader, cache);

        var request = new ListPublicEquipmentListingsRequest { Sort = "recommended" };
        var result = await service.ListPublicListingsAsync(request, AnonymousCallerContext.Instance);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, reader.CallCount);
        Assert.Null(reader.LastUserId);
        Assert.Equal(0, cache.GetOrCreateCalls); // Bypasses response cache!
        Assert.Null(repo.LastCapturedCriteria!.ExcludedOwnerUserId);
    }

    [Fact]
    public async Task ListPublicListings_ServicePrincipal_TreatedAsAnonymousGlobal()
    {
        var repo = new FakeListingRepository();
        var reader = new FakeRecommendationReader();
        var cache = new SpyReadCache();
        var service = CreateService(repo, reader, cache);

        var request = new ListPublicEquipmentListingsRequest { Sort = "recommended" };
        var result = await service.ListPublicListingsAsync(request, new ServiceTestCaller());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, reader.CallCount);
        Assert.Null(reader.LastUserId);
        Assert.Equal(0, cache.GetOrCreateCalls);
        Assert.Null(repo.LastCapturedCriteria!.ExcludedOwnerUserId);
    }

    [Fact]
    public async Task ListPublicListings_HumanUser_ExtractsUserIdAndAppliesOwnerExclusion()
    {
        var humanUserId = Guid.NewGuid();
        var repo = new FakeListingRepository();
        var reader = new FakeRecommendationReader();
        var cache = new SpyReadCache();
        var service = CreateService(repo, reader, cache);

        var request = new ListPublicEquipmentListingsRequest { Sort = "recommended" };
        var result = await service.ListPublicListingsAsync(request, new HumanTestCaller(humanUserId));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, reader.CallCount);
        Assert.Equal(humanUserId, reader.LastUserId);
        Assert.Equal(0, cache.GetOrCreateCalls);
        Assert.Equal(humanUserId, repo.LastCapturedCriteria!.ExcludedOwnerUserId);
    }

    [Fact]
    public async Task ListPublicListings_KeywordPlusRecommended_BypassesRankingReaderAndUsesCache()
    {
        var repo = new FakeListingRepository();
        var reader = new FakeRecommendationReader();
        var cache = new SpyReadCache();
        var service = CreateService(repo, reader, cache);

        var request = new ListPublicEquipmentListingsRequest { Q = "sony", Sort = "recommended" };
        var result = await service.ListPublicListingsAsync(request, new HumanTestCaller(Guid.NewGuid()));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, reader.CallCount); // Ranking reader was NOT called!
        Assert.Equal(1, cache.GetOrCreateCalls); // Uses response cache!
        Assert.Null(repo.LastCapturedCriteria!.ExcludedOwnerUserId); // No recommendation owner exclusion
    }

    [Fact]
    public async Task ListPublicListings_PropagatesCancellation()
    {
        var repo = new FakeListingRepository();
        var reader = new FakeRecommendationReader { ObserveCancellation = true };
        var service = CreateService(repo, reader);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = new ListPublicEquipmentListingsRequest { Sort = "recommended" };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.ListPublicListingsAsync(request, AnonymousCallerContext.Instance, cts.Token);
        });
    }

    [Fact]
    public async Task ListPublicListings_WhenReaderFailsOrTimesOut_FallsBackToDefaultWithoutThrowing()
    {
        var repo = new FakeListingRepository();
        var reader = new FakeRecommendationReader { ThrowTimeout = true };
        var service = CreateService(repo, reader);

        var request = new ListPublicEquipmentListingsRequest { Sort = "recommended" };
        var result = await service.ListPublicListingsAsync(request, AnonymousCallerContext.Instance);

        Assert.True(result.IsSuccess);
        Assert.Null(repo.LastCapturedCriteria!.PersonalScores);
        Assert.Null(repo.LastCapturedCriteria.GlobalScores);
    }
}
