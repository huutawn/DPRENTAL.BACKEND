using System.Security.Claims;
using Dprental.InventoryService.Application.DTOs.Catalog;
using System.Text.Json;
using Dprental.InventoryService.Api.Security;
using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Contracts.Behavior;
using Dprental.InventoryService.Application.Kafka.Transport;
using Dprental.InventoryService.Application.Options;
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

public sealed class BehaviorEventRecordingAndOutboxTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    private sealed class TestOutboxStore : IOutboxStore
    {
        public List<OutboxMessage> Messages { get; } = [];
        public int SaveChangesCalls { get; private set; }
        public TimeSpan DelayOnSave { get; set; } = TimeSpan.Zero;
        public bool ThrowOnSave { get; set; }
        public Func<CancellationToken, Task>? SaveAction { get; set; }

        public void Add(OutboxMessage message) => Messages.Add(message);

        public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalls++;
            if (SaveAction is not null) await SaveAction(cancellationToken);
            if (DelayOnSave > TimeSpan.Zero)
            {
                await Task.Delay(DelayOnSave, cancellationToken);
            }
            if (ThrowOnSave)
            {
                throw new InvalidOperationException("Simulated database failure");
            }
        }

        public Task<OutboxMessage?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Messages.FirstOrDefault(m => m.Id == id));

        public Task<IReadOnlyList<OutboxMessage>> ClaimPendingBatchAsync(int batchSize, Guid leaseToken, TimeSpan leaseDuration, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public Task<bool> MarkAsPublishedAsync(Guid id, Guid leaseToken, DateTimeOffset publishedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> RecordFailureAsync(Guid id, Guid leaseToken, string errorMessage, DateTimeOffset nextAttemptAt, DateTimeOffset? leaseExpiry = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> RequeueAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<int> CleanupPublishedBehaviorMessagesAsync(DateTimeOffset publishedBefore, int maxBatchSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Cleanup is tested against the real PostgreSQL store.");

    }

    private sealed class FakeListingRepo : IEquipmentListingRepository
    {
        public Dictionary<Guid, EquipmentListing> Store { get; } = [];

        public Task<EquipmentListing?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) ? l : null);

        public Task<EquipmentListing?> GetCalendarSnapshotAsync(Guid listingId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(listingId, out var l) ? l : null);

        public Task<PublicEquipmentListingSearchResult> SearchPublicAsync(EquipmentListingSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            var matching = Store.Values.Where(l => l.Status == EquipmentListingStatus.Active).ToList();
            var items = matching
                .Skip(criteria.Skip)
                .Take(criteria.Take)
                .Select(l => new PublicEquipmentListingItem(l, 1, 1))
                .ToList();
            return Task.FromResult(new PublicEquipmentListingSearchResult(items, matching.Count));
        }

        public Task<OwnerEquipmentListingSearchResult> SearchOwnerAsync(OwnerEquipmentListingSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<EquipmentListing?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) && l.OwnerUserId == ownerUserId ? l : null);
        public Task<EquipmentListing?> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) && l.Status == EquipmentListingStatus.Active ? l : null);
        public Task<IReadOnlyList<EquipmentListingBatchItem>> GetBatchByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EquipmentListingBatchItem>>([]);
        public Task<EquipmentListingStatisticsResult> GetListingStatisticsAsync(EquipmentListingStatisticsCriteria criteria, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsPublicAsync(AssetSearchFilters filters, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<int>>([]);
        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsOwnerAsync(Guid ownerUserId, AssetSearchFilters filters, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<int> GetTotalQuantityAsync(Guid listingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(1);
        public Task<IReadOnlyList<Guid>> GetListingIdsByOwnerKeysetAsync(Guid ownerUserId, Guid? afterListingId, int limit, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public void Add(EquipmentListing listing) => Store[listing.Id] = listing;
        public void Update(EquipmentListing listing) => Store[listing.Id] = listing;
    }

    private sealed class FakeCatalogClient : ICatalogServiceClient
    {
        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(id, "CAM", "Camera", null, 1, DateTime.UtcNow, null, DateTime.UtcNow)));
        public Task<Result<CatalogEquipmentTypeResponse>> GetEquipmentTypeAsync(Guid id, CancellationToken cancellationToken = default) =>
            GetPublishedEquipmentTypeAsync(id, cancellationToken);
        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogBrandResponse(id, "Sony", "SONY", true)));
        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
        public Task<Result<CachedRead<IReadOnlyList<Guid>>>> GetEquipmentTypeIdsByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CachedRead<IReadOnlyList<Guid>>([], null)));
    }

    private sealed class InMemoryTestCache : IInventoryReadCache
    {
        private readonly Dictionary<string, object> _entries = new();
        public bool IsEnabled => true;
        public Task<CacheReadResult<T>> GetAsync<T>(string scope, string queryType, string inputHash, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CacheReadResult<T>(default, "token", false));
        public Task<bool> SetAsync<T>(string scope, string queryType, string inputHash, T value, string expectedGenerationToken, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
        {
            _entries[$"{scope}:{queryType}:{inputHash}"] = value!;
            return Task.FromResult(true);
        }
        public Task<IReadOnlyDictionary<string, CacheReadResult<T>>> GetBulkAsync<T>(string scope, string queryType, IReadOnlyList<string> inputHashes, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, CacheReadResult<T>>>(new Dictionary<string, CacheReadResult<T>>());
        public Task SetBulkAsync<T>(string scope, string queryType, IReadOnlyList<(string InputHash, T Value, DateTimeOffset ExpiresAtUtc)> items, string expectedGenerationToken, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public async Task<Result<T>> GetOrCreateAsync<T>(string scope, string queryType, string inputHash, Func<string, CancellationToken, Task<Result<CachedRead<T>>>> factory, CancellationToken cancellationToken = default)
        {
            string key = $"{scope}:{queryType}:{inputHash}";
            if (_entries.TryGetValue(key, out var cached))
            {
                return Result.Success((T)cached);
            }
            var res = await factory("token", cancellationToken);
            if (res.IsSuccess)
            {
                _entries[key] = res.Value.Value!;
                return Result.Success(res.Value.Value!);
            }
            return res.Error;
        }
        public Task<Result<IReadOnlyDictionary<CacheTarget, T>>> GetOrCreateBulkAsync<T>(IReadOnlyList<CacheTarget> targets, Func<IReadOnlyList<CacheTarget>, CancellationToken, Task<Result<IReadOnlyDictionary<CacheTarget, CachedRead<T>>>>> factory, CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<IReadOnlyDictionary<CacheTarget, T>>>(new Dictionary<CacheTarget, T>());
        public Task InvalidateScopeAsync(string scope, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InvalidateScopesAsync(IEnumerable<string> scopes, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static (PublicEquipmentListingService Service, TestOutboxStore OutboxStore, FakeListingRepo Repo) CreateTestFixture(
        IInventoryReadCache? cache = null,
        TimeProvider? timeProvider = null)
    {
        var repo = new FakeListingRepo();
        var outboxStore = new TestOutboxStore();
        var clock = timeProvider ?? TimeProvider.System;
        var recorder = new BehaviorEventRecorder(outboxStore, clock, NullLogger<BehaviorEventRecorder>.Instance);

        var mediaOptions = Options.Create(new MediaServiceOptions { PublicGatewayUrl = "https://cdn.example.com" });
        var queryValidator = new AssetSearchQueryValidator(clock);
        var catalogClient = new FakeCatalogClient();
        var filterResolver = new AssetSearchFilterResolver(catalogClient);

        var service = new PublicEquipmentListingService(
            repo,
            queryValidator,
            filterResolver,
            mediaOptions,
            clock,
            null,
            null,
            null,
            null,
            cache ?? NoOpInventoryReadCache.Instance,
            null,
            null,
            null,
            recorder);

        return (service, outboxStore, repo);
    }

    [Fact]
    public async Task ListingViewed_EmittedOnActiveDetail_Success_BothCacheHitAndMiss_ValidatesSchema()
    {
        var memCache = new InMemoryTestCache();
        var (service, outbox, repo) = CreateTestFixture(cache: memCache);

        var listingId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();

        var listing = EquipmentListing.Create(
            listingId, ownerId, typeId, Guid.NewGuid(),
            "Sony FX3", null, "FX3", 1, "{}", 500_000m, null, null, "VND", FixedNow, 2024).Value;
        listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        var asset = InventoryAsset.Create(Guid.NewGuid(), listingId, "SN-FX3-001", FixedNow).Value;
        listing.Assets.Add(asset);
        repo.Store[listingId] = listing;

        var callerContext = new InventoryCallerContext(null, hasValidApiKey: true);

        // 1. Cache miss (lần gọi đầu tiên)
        var res1 = await service.GetPublicListingByIdAsync(listingId, callerContext);
        Assert.True(res1.IsSuccess);
        Assert.Single(outbox.Messages);

        var msg1 = outbox.Messages[0];
        Assert.Equal(KafkaTopics.InventoryBehaviorEventsV1, msg1.Topic);
        Assert.Equal(listingId.ToString("D"), msg1.KafkaKey); // Anonymous view uses listingId as partition key

        // 2. Cache hit (lần gọi thứ hai)
        var res2 = await service.GetPublicListingByIdAsync(listingId, callerContext);
        Assert.True(res2.IsSuccess);
        Assert.Equal(2, outbox.Messages.Count);

        var msg2 = outbox.Messages[1];
        Assert.NotEqual(msg1.Id, msg2.Id); // ID độc lập cho mỗi lần ghi nhận

        // 3. Schema validation Draft 2020-12
        var schema = RecommendationContractResources.LoadSchema("listing-viewed.schema.json");
        using var doc1 = JsonDocument.Parse(msg1.Payload);
        var eval1 = schema.Evaluate(doc1.RootElement, new Json.Schema.EvaluationOptions { RequireFormatValidation = true });
        Assert.True(eval1.IsValid);

        using var doc2 = JsonDocument.Parse(msg2.Payload);
        var eval2 = schema.Evaluate(doc2.RootElement, new Json.Schema.EvaluationOptions { RequireFormatValidation = true });
        Assert.True(eval2.IsValid);
    }

    [Fact]
    public async Task ListingViewed_HumanJwt_SetsUserIdAndKafkaKey()
    {
        var (service, outbox, repo) = CreateTestFixture();

        var listingId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var listing = EquipmentListing.Create(
            listingId, Guid.NewGuid(), typeId, Guid.NewGuid(),
            "Camera", null, "Cam 1", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        repo.Store[listingId] = listing;

        var claims = new[]
        {
            new Claim("sub", userId.ToString("D")),
            new Claim("u", "usr")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);
        var callerContext = new InventoryCallerContext(principal, hasValidApiKey: true, correlationId: "corr-human-view");

        var res = await service.GetPublicListingByIdAsync(listingId, callerContext);
        Assert.True(res.IsSuccess);
        Assert.Single(outbox.Messages);

        var msg = outbox.Messages[0];
        Assert.Equal(userId.ToString("D"), msg.KafkaKey); // Human uses userId as Kafka partition key

        var doc = JsonDocument.Parse(msg.Payload);
        Assert.Equal("corr-human-view", doc.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(userId.ToString("D"), doc.RootElement.GetProperty("payload").GetProperty("userId").GetString());
        Assert.Equal(listingId.ToString("D"), doc.RootElement.GetProperty("payload").GetProperty("equipmentListingId").GetString());
        Assert.Equal(typeId.ToString("D"), doc.RootElement.GetProperty("payload").GetProperty("equipmentTypeId").GetString());

        var schema = RecommendationContractResources.LoadSchema("listing-viewed.schema.json");
        using var humanDoc = JsonDocument.Parse(msg.Payload);
        var eval = schema.Evaluate(humanDoc.RootElement, new Json.Schema.EvaluationOptions { RequireFormatValidation = true });
        Assert.True(eval.IsValid);
    }

    [Fact]
    public async Task ListingViewed_ServiceJwt_And_Prefetch_DoNotEmitEvents()
    {
        var (service, outbox, repo) = CreateTestFixture();

        var listingId = Guid.NewGuid();
        var listing = EquipmentListing.Create(
            listingId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "Camera", null, "Cam 1", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        repo.Store[listingId] = listing;

        // 1. Service JWT (claim u=svc)
        var svcClaims = new[] { new Claim("u", "svc"), new Claim("sub", Guid.NewGuid().ToString("D")) };
        var svcPrincipal = new ClaimsPrincipal(new ClaimsIdentity(svcClaims, "Bearer"));
        var svcContext = new InventoryCallerContext(svcPrincipal, hasValidApiKey: true);

        var resSvc = await service.GetPublicListingByIdAsync(listingId, svcContext);
        Assert.True(resSvc.IsSuccess);
        Assert.Empty(outbox.Messages);

        // 2. Prefetch request
        var prefetchContext = new InventoryCallerContext(null, hasValidApiKey: true, isPrefetch: true);
        var resPrefetch = await service.GetPublicListingByIdAsync(listingId, prefetchContext);
        Assert.True(resPrefetch.IsSuccess);
        Assert.Empty(outbox.Messages);
    }

    [Fact]
    public async Task SearchPerformed_Page1WithFilterOrKeyword_EmitsEvent_AndMatchesSchema()
    {
        var (service, outbox, repo) = CreateTestFixture();

        var typeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        var listing = EquipmentListing.Create(
            Guid.NewGuid(), Guid.NewGuid(), typeId, brandId,
            "Sony FX3 Camera", null, "FX3", 1, "{}", 200_000m, null, null, "VND", FixedNow, 2024).Value;
        listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        repo.Store[listing.Id] = listing;

        var callerContext = new InventoryCallerContext(null, hasValidApiKey: true, correlationId: "corr-search-1");

        var request = new ListPublicEquipmentListingsRequest
        {
            Page = 1,
            PageSize = 20,
            Q = "Sony",
            EquipmentTypeId = typeId,
            BrandId = brandId,
            ProvinceCode = "01"
        };

        var result = await service.ListPublicListingsAsync(request, callerContext);
        Assert.True(result.IsSuccess);
        Assert.Single(outbox.Messages);

        var msg = outbox.Messages[0];
        Assert.Equal(KafkaTopics.InventoryBehaviorEventsV1, msg.Topic);
        // Anonymous search partition key uses messageId
        Assert.Equal(msg.Id.ToString("D"), msg.KafkaKey);

        var doc = JsonDocument.Parse(msg.Payload);
        Assert.Equal("SearchPerformed", doc.RootElement.GetProperty("messageType").GetString());
        Assert.Equal("corr-search-1", doc.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal("InventoryService", doc.RootElement.GetProperty("producer").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());

        // Timestamp must end with Z
        string occurredAt = doc.RootElement.GetProperty("occurredAt").GetString()!;
        Assert.EndsWith("Z", occurredAt, StringComparison.Ordinal);

        var payload = doc.RootElement.GetProperty("payload");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("userId").ValueKind);
        Assert.Equal(typeId.ToString("D"), payload.GetProperty("equipmentTypeId").GetString());
        Assert.Equal("Sony", payload.GetProperty("query").GetString());
        Assert.Equal(1, payload.GetProperty("resultCount").GetInt32());

        var filterCtx = payload.GetProperty("filterContext");
        Assert.Equal(brandId.ToString("D"), filterCtx.GetProperty("brandId").GetString());
        Assert.Equal("01", filterCtx.GetProperty("provinceCode").GetString());
        Assert.Equal(1, filterCtx.GetProperty("page").GetInt32());
        Assert.Equal(20, filterCtx.GetProperty("pageSize").GetInt32());

        // Validate against Draft 2020-12 schema
        var schema = RecommendationContractResources.LoadSchema("search-performed.schema.json");
        using var searchDoc = JsonDocument.Parse(msg.Payload);
        var eval = schema.Evaluate(searchDoc.RootElement, new Json.Schema.EvaluationOptions { RequireFormatValidation = true });
        Assert.True(eval.IsValid);
    }

    [Fact]
    public async Task SearchPerformed_ZeroResultsWithFilter_StillEmitsEventWithResultCountZero()
    {
        var (service, outbox, _) = CreateTestFixture();

        var callerContext = new InventoryCallerContext(null, hasValidApiKey: true);
        var request = new ListPublicEquipmentListingsRequest
        {
            Page = 1,
            PageSize = 20,
            Q = "NonExistentEquipment12345"
        };

        var result = await service.ListPublicListingsAsync(request, callerContext);
        Assert.True(result.IsSuccess);
        Assert.Single(outbox.Messages);

        var msg = outbox.Messages[0];
        var doc = JsonDocument.Parse(msg.Payload);
        Assert.Equal(0, doc.RootElement.GetProperty("payload").GetProperty("resultCount").GetInt32());

        var schema = RecommendationContractResources.LoadSchema("search-performed.schema.json");
        using var zeroDoc = JsonDocument.Parse(msg.Payload);
        var eval = schema.Evaluate(zeroDoc.RootElement, new Json.Schema.EvaluationOptions { RequireFormatValidation = true });
        Assert.True(eval.IsValid);
    }

    [Fact]
    public async Task SearchPerformed_Page2_Or_NoFilters_DoesNotEmitEvents()
    {
        var (service, outbox, _) = CreateTestFixture();
        var callerContext = new InventoryCallerContext(null, hasValidApiKey: true);

        // 1. Page 2 with query -> do not emit
        var reqPage2 = new ListPublicEquipmentListingsRequest
        {
            Page = 2,
            PageSize = 20,
            Q = "Camera"
        };
        var resPage2 = await service.ListPublicListingsAsync(reqPage2, callerContext);
        Assert.True(resPage2.IsSuccess);
        Assert.Empty(outbox.Messages);

        // 2. Page 1 without any query or filter (pure discovery list with default or sort)
        var reqNoFilters = new ListPublicEquipmentListingsRequest
        {
            Page = 1,
            PageSize = 20,
            Sort = "created_desc"
        };
        var resNoFilters = await service.ListPublicListingsAsync(reqNoFilters, callerContext);
        Assert.True(resNoFilters.IsSuccess);
        Assert.Empty(outbox.Messages);
    }

    [Fact]
    public async Task OutboxSaveTimeout_200ms_DoesNotFailGetResult()
    {
        var (service, outbox, repo) = CreateTestFixture();
        outbox.DelayOnSave = TimeSpan.FromMilliseconds(350); // Exceeds 200ms budget

        var listingId = Guid.NewGuid();
        var listing = EquipmentListing.Create(
            listingId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "Camera", null, "Cam", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        repo.Store[listingId] = listing;

        var callerContext = new InventoryCallerContext(null, hasValidApiKey: true);

        // Reading detail must succeed even when outbox save times out
        var result = await service.GetPublicListingByIdAsync(listingId, callerContext);
        Assert.True(result.IsSuccess);
        Assert.Equal(listingId, result.Value.Id);
    }

    [Fact]
    public async Task RequestCancellation_PropagatesOutward()
    {
        var (service, _, repo) = CreateTestFixture();

        var listingId = Guid.NewGuid();
        var listing = EquipmentListing.Create(
            listingId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "Camera", null, "Cam", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        repo.Store[listingId] = listing;

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Request canceled upfront

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetPublicListingByIdAsync(listingId, AnonymousCallerContext.Instance, cts.Token));
    }

    [Fact]
    public async Task CancellationDuringSave_PropagatesEvenWhenProviderWrapsIt()
    {
        var store = new TestOutboxStore();
        using var client = new CancellationTokenSource();
        store.SaveAction = _ =>
        {
            client.Cancel();
            throw new InvalidOperationException("Provider wrapped cancellation", new OperationCanceledException(client.Token));
        };
        var recorder = new BehaviorEventRecorder(store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.RecordListingViewedAsync(
            Guid.NewGuid(), Guid.NewGuid(), AnonymousCallerContext.Instance, client.Token));
    }
}
