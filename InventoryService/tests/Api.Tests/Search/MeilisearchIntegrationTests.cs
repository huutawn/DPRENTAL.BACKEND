using Dprental.InventoryService.Api.Tests.Integration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Search;
using Dprental.InventoryService.Application.Search.Adapters;
using Dprental.InventoryService.Application.Search.Workers;
using Dprental.InventoryService.Application.Search.DependencyInjection;
using Dprental.InventoryService.Application.Search.Models;
using Meilisearch;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Common.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.InventoryService.Api.Tests.Search;

public sealed partial class MeilisearchIntegrationTests : IAsyncLifetime, IAsyncDisposable
{
    private const string MeiliUrl = "http://localhost:7700";
    private const string MasterKey = "dev_meili_master_key_32_chars_long";

    private readonly MeilisearchClient _client;
    private readonly MeilisearchOptions _options;
    private readonly MeilisearchIndexProvisioner _provisioner;
    private MeilisearchInventoryIndexService _searchService = null!;
    private readonly PostgresFixture _postgres = new();
    private InventoryServiceDbContext _db = null!;
    private SearchProjectionStore _store = null!;

    public MeilisearchIntegrationTests()
    {
        _options = new MeilisearchOptions
        {
            Url = MeiliUrl,
            ApiKey = MasterKey,
            PublicListingsIndex = $"test_public_{Guid.NewGuid():N}",
            OwnerListingsIndex = $"test_owner_listings_{Guid.NewGuid():N}",
            OwnerAssetsIndex = $"test_owner_assets_{Guid.NewGuid():N}",
            CandidateLimit = 5000
        };

        var optionsWrapper = Microsoft.Extensions.Options.Options.Create(_options);
        _client = new MeilisearchClient(_options.Url, _options.ApiKey);

        _provisioner = new MeilisearchIndexProvisioner(
            _client,
            optionsWrapper,
            NullLogger<MeilisearchIndexProvisioner>.Instance);

    }

    public async Task InitializeAsync()
    {
        Assert.True(await _client.IsHealthyAsync(), "Real Meilisearch is required; this suite must not silently skip.");
        await _postgres.InitializeAsync();
        _db = new InventoryServiceDbContext(new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_postgres.ConnectionString).Options);
        _store = new SearchProjectionStore(_db);
        var generation = SearchGeneration.CreateInitial(_options.PublicListingsIndex, _options.OwnerListingsIndex, _options.OwnerAssetsIndex, DateTimeOffset.UtcNow);
        generation.MarkReady(DateTimeOffset.UtcNow);
        await _store.SaveGenerationAsync(generation);
        _searchService = new MeilisearchInventoryIndexService(_client, _store,
            Options.Create(_options), NullLogger<MeilisearchInventoryIndexService>.Instance);
        await _provisioner.EnsureIndexesAndSettingsAsync();
    }

    public async Task DisposeAsync()
    {
        await _client.DeleteIndexAsync(_options.PublicListingsIndex);
        await _client.DeleteIndexAsync(_options.OwnerListingsIndex);
        await _client.DeleteIndexAsync(_options.OwnerAssetsIndex);
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    [Fact]
    public async Task Provisioner_ConfiguresSettingsAndTypoTolerance()
    {

        var assetIndex = _client.Index(_options.OwnerAssetsIndex);
        var settings = await assetIndex.GetSettingsAsync();

        Assert.Contains("serialNumber", settings.SearchableAttributes);
        Assert.Contains("model", settings.SearchableAttributes);
        Assert.NotNull(settings.TypoTolerance);
        Assert.Contains("serialNumber", settings.TypoTolerance.DisableOnAttributes);
        Assert.Equal(100000, settings.Pagination?.MaxTotalHits);
    }

    [Fact]
    public async Task VietnameseSearch_AccentedAndUnaccented_MatchesCorrectly()
    {

        var publicIndex = _client.Index(_options.PublicListingsIndex);
        var doc1 = new PublicListingIndexDocument
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Máy ảnh Sony Alpha A7 IV",
            Model = "ILCE-7M4",
            Description = "Máy ảnh mirrorless full-frame chuyên nghiệp",
            EquipmentTypeId = Guid.NewGuid().ToString(),
            BrandId = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var doc2 = new PublicListingIndexDocument
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Đèn quay phim Godox SL60W LED",
            Model = "SL60W",
            Description = "Đèn studio ánh sáng trắng liên tục",
            EquipmentTypeId = Guid.NewGuid().ToString(),
            BrandId = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var task = await publicIndex.AddDocumentsAsync(new[] { doc1, doc2 }, "id");
        await _client.WaitForTaskAsync(task.TaskUid, timeoutMs: 15000);

        // 1. Unaccented search "may anh"
        var res1 = await _searchService.SearchPublicListingCandidatesAsync(
            new PublicListingSearchIndexQuery("may anh"));
        Assert.True(res1.IsSuccess);
        Assert.Contains(Guid.Parse(doc1.Id), res1.Value.CandidateIds);
        Assert.DoesNotContain(Guid.Parse(doc2.Id), res1.Value.CandidateIds);

        // 2. Accented search "máy ảnh"
        var res2 = await _searchService.SearchPublicListingCandidatesAsync(
            new PublicListingSearchIndexQuery("máy ảnh"));
        Assert.True(res2.IsSuccess);
        Assert.Contains(Guid.Parse(doc1.Id), res2.Value.CandidateIds);

        // 3. "den quay" (unaccented đ -> d)
        var res3 = await _searchService.SearchPublicListingCandidatesAsync(
            new PublicListingSearchIndexQuery("den quay"));
        Assert.True(res3.IsSuccess);
        Assert.Contains(Guid.Parse(doc2.Id), res3.Value.CandidateIds);
        Assert.DoesNotContain(Guid.Parse(doc1.Id), res3.Value.CandidateIds);

        // 4. "đèn quay" (accented with đ)
        var res4 = await _searchService.SearchPublicListingCandidatesAsync(
            new PublicListingSearchIndexQuery("đèn quay"));
        Assert.True(res4.IsSuccess);
        Assert.Contains(Guid.Parse(doc2.Id), res4.Value.CandidateIds);

        // 5. Typo tolerance: "alhpa" -> "Alpha"
        var res5 = await _searchService.SearchPublicListingCandidatesAsync(
            new PublicListingSearchIndexQuery("alhpa"));
        Assert.True(res5.IsSuccess);
        Assert.Contains(Guid.Parse(doc1.Id), res5.Value.CandidateIds);

        // 6. Prefix: "godo" -> "Godox"
        var res6 = await _searchService.SearchPublicListingCandidatesAsync(
            new PublicListingSearchIndexQuery("godo"));
        Assert.True(res6.IsSuccess);
        Assert.Contains(Guid.Parse(doc2.Id), res6.Value.CandidateIds);
    }

    [Fact]
    public async Task SerialNumberSearch_TypoToleranceDisabled_RequiresExactMatch()
    {

        var ownerId = Guid.NewGuid();
        var assetIndex = _client.Index(_options.OwnerAssetsIndex);

        var asset1 = new OwnerAssetIndexDocument
        {
            Id = Guid.NewGuid().ToString(),
            OwnerUserId = ownerId.ToString(),
            EquipmentListingId = Guid.NewGuid().ToString(),
            SerialNumber = "SN-SONY-7M4-001",
            Model = "ILCE-7M4",
            EquipmentTypeId = Guid.NewGuid().ToString(),
            BrandId = Guid.NewGuid().ToString(),
            ManufactureYear = 2024,
            AcceptingBookings = true,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var task = await assetIndex.AddDocumentsAsync(new[] { asset1 }, "id");
        await _client.WaitForTaskAsync(task.TaskUid, timeoutMs: 15000);

        // Exact match succeeds
        var exactRes = await _searchService.SearchOwnerAssetCandidatesAsync(
            new OwnerAssetSearchIndexQuery(ownerId, "SN-SONY-7M4-001"));
        Assert.True(exactRes.IsSuccess);
        Assert.Contains(Guid.Parse(asset1.Id), exactRes.Value.CandidateIds);

        // Slight typo in serial number (001 -> 002) MUST NOT match
        var typoRes = await _searchService.SearchOwnerAssetCandidatesAsync(
            new OwnerAssetSearchIndexQuery(ownerId, "SN-SONY-7M4-002"));
        Assert.True(typoRes.IsSuccess);
        Assert.Empty(typoRes.Value.CandidateIds);
    }

    [Fact]
    public async Task Fallback_WhenMeilisearchUnavailable_ReturnsFailureGracefully()
    {
        // Bad client pointing to dead port
        var badOptions = new MeilisearchOptions
        {
            Url = "http://127.0.0.1:59999",
            ApiKey = "none",
            TimeoutSeconds = 1
        };
        var badClient = new MeilisearchClient(badOptions.Url, badOptions.ApiKey);
        var badService = new MeilisearchInventoryIndexService(
            badClient,
            _store,
            Microsoft.Extensions.Options.Options.Create(badOptions),
            NullLogger<MeilisearchInventoryIndexService>.Instance);

        var isAvailable = await badService.IsAvailableAsync();
        Assert.False(isAvailable);

        var result = await badService.SearchPublicListingCandidatesAsync(
            new PublicListingSearchIndexQuery("test"));

        // Returns failure Result (soft error) instead of throwing exception, triggering SQL fallback
        Assert.True(result.IsFailure);
        Assert.Equal("MEILISEARCH_QUERY_FAILED", result.Error.Code);
    }

    [Fact]
    public async Task SearchProjectionMessageHandler_StoresJob_AndWorkerSyncsToMeilisearch()
    {

        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_postgres.ConnectionString)
            .Options;
        await using var db = new InventoryServiceDbContext(options);
        var listingId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var listing = EquipmentListing.Create(
            listingId,
            ownerId,
            typeId,
            brandId,
            "Micro thu âm Rode Wireless GO II",
            "Micro cài áo không dây nhỏ gọn",
            "Wireless GO II",
            schemaVersion: 1,
            specValues: "{}",
            dailyRate: 150000,
            weeklyRate: null,
            monthlyRate: null,
            currency: "VND",
            now: now,
            manufactureYear: 2023).Value;
        // Activate listing
        listing.UpdateStatus(EquipmentListingStatus.Active, hasUnitAcceptingBookings: true, now);
        db.EquipmentListings.Add(listing);
        await db.SaveChangesAsync();

        var store = new SearchProjectionStore(db);

        // 1. Consumer handles Kafka envelope
        var payload = new Dprental.InventoryService.Application.Kafka.Contracts.SearchProjectionChangedPayload(
            "EquipmentListing",
            listingId,
            "Upsert",
            1,
            now);

        var handler = new Dprental.InventoryService.Application.Kafka.Transport.SearchProjectionMessageHandler(store);
        var envelope = Dprental.InventoryService.Application.Kafka.Contracts.MessageEnvelope.CreateRoot(
            "InventoryListingProjectionChanged",
            listingId.ToString(),
            payload,
            version: 1,
            occurredAt: now);

        var handleResult = await handler.HandleAsync(envelope, default);
        Assert.True(handleResult.IsSuccess);

        // Verify job in DB
        var job = await db.SearchProjectionJobs.FirstOrDefaultAsync(j => j.EntityId == listingId);
        Assert.NotNull(job);
        Assert.Equal(SearchProjectionJob.StatusPending, job.Status);

        // 2. Worker processes job
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDbContext<InventoryServiceDbContext>(o => o.UseNpgsql(_postgres.ConnectionString));
        services.AddScoped<ISearchProjectionStore, SearchProjectionStore>();
        services.AddSingleton<IInventoryIndexProvisioner>(_provisioner);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();

        var worker = new SearchProjectionWorker(
            scopeFactory,
            _client,
            Microsoft.Extensions.Options.Options.Create(_options),
            TimeProvider.System,
            NullLogger<SearchProjectionWorker>.Instance);

        int processed = await worker.ProcessBatchAsync(CancellationToken.None);
        Assert.Equal(1, processed);
        var finishedJob = await db.SearchProjectionJobs.AsNoTracking().FirstOrDefaultAsync(j => j.EntityId == listingId);
        Assert.NotNull(finishedJob);
        Assert.Equal(SearchProjectionJob.StatusCompleted, finishedJob.Status);

        var searchResult = await _searchService.SearchPublicListingCandidatesAsync(
            new PublicListingSearchIndexQuery("Rode Wireless"));
        Assert.True(searchResult.IsSuccess);
        Assert.Contains(listingId, searchResult.Value.CandidateIds);
    }

    [Fact]
    public async Task OnlineBackfill_WithAtomicSwap_PopulatesLiveIndexesAndMarksReady()
    {

        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_postgres.ConnectionString)
            .Options;
        await using var db = new InventoryServiceDbContext(options);
        var now = DateTimeOffset.UtcNow;

        // Seed 3 active listings
        for (int i = 1; i <= 3; i++)
        {
            var l = EquipmentListing.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                $"Ống kính Canon RF {i}00mm",
                $"Mô tả ống kính {i}",
                $"RF-{i}00",
                schemaVersion: 1,
                specValues: "{}",
                dailyRate: 300000,
                weeklyRate: null,
                monthlyRate: null,
                currency: "VND",
                now: now,
                manufactureYear: 2023).Value;
            l.UpdateStatus(EquipmentListingStatus.Active, hasUnitAcceptingBookings: true, now);
            db.EquipmentListings.Add(l);
        }
        await db.SaveChangesAsync();

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDbContext<InventoryServiceDbContext>(o => o.UseNpgsql(_postgres.ConnectionString));
        services.AddScoped<ISearchProjectionStore, SearchProjectionStore>();
        services.AddSingleton<IInventoryIndexProvisioner>(_provisioner);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();

        var backfillService = new MeilisearchBackfillService(
            scopeFactory,
            _client,
            Microsoft.Extensions.Options.Options.Create(_options),
            TimeProvider.System,
            NullLogger<MeilisearchBackfillService>.Instance);

        var result = await backfillService.BackfillAsync(CancellationToken.None);
        Assert.Equal(3, result.PublicListingsCount);
        Assert.Equal(3, result.OwnerListingsCount);

        // Verify live index has documents
        var candidates = await _searchService.SearchPublicListingCandidatesAsync(
            new PublicListingSearchIndexQuery("Canon RF"));
        Assert.True(candidates.IsSuccess);
        Assert.Equal(3, candidates.Value.CandidateIds.Count);

        // Verify SearchGeneration marked ready
        var store = new SearchProjectionStore(db);
        var gen = await store.GetGenerationAsync();
        Assert.NotNull(gen);
        Assert.True(gen.IsReady);
    }

    [Fact]
    public async Task Worker_AssetFanout_EnqueuesChildAssetJobs()
    {

        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_postgres.ConnectionString)
            .Options;
        await using var db = new InventoryServiceDbContext(options);
        var now = DateTimeOffset.UtcNow;

        var listingId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var listing = EquipmentListing.Create(
            listingId,
            ownerId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Đèn Aputure 600d Pro",
            "Đèn công suất cao",
            "600d Pro",
            schemaVersion: 1,
            specValues: "{}",
            dailyRate: 800000,
            weeklyRate: null,
            monthlyRate: null,
            currency: "VND",
            now: now,
            manufactureYear: 2024).Value;
        db.EquipmentListings.Add(listing);

        // Add 2 assets to listing
        var a1 = InventoryAsset.Create(Guid.NewGuid(), listingId, "SN-APUTURE-01", now, 2024).Value;
        var a2 = InventoryAsset.Create(Guid.NewGuid(), listingId, "SN-APUTURE-02", now, 2024).Value;
        db.Assets.Add(a1);
        db.Assets.Add(a2);
        await db.SaveChangesAsync();

        var store = new SearchProjectionStore(db);
        // Enqueue listing job with needsAssetFanout = true
        await store.UpsertJobAsync("EquipmentListing", listingId, "Upsert", desiredRevision: 10, needsAssetFanout: true, now);

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDbContext<InventoryServiceDbContext>(o => o.UseNpgsql(_postgres.ConnectionString));
        services.AddScoped<ISearchProjectionStore, SearchProjectionStore>();
        services.AddSingleton<IInventoryIndexProvisioner>(_provisioner);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();

        var worker = new SearchProjectionWorker(
            scopeFactory,
            _client,
            Microsoft.Extensions.Options.Options.Create(_options),
            TimeProvider.System,
            NullLogger<SearchProjectionWorker>.Instance);

        await worker.ProcessBatchAsync(CancellationToken.None);

        // Verify child jobs were enqueued for both assets
        var childJobs = await db.SearchProjectionJobs
            .Where(j => j.EntityType == "InventoryAsset")
            .ToListAsync();
        Assert.Equal(2, childJobs.Count);
        Assert.Contains(childJobs, j => j.EntityId == a1.Id && j.DesiredRevision == 10);
        Assert.Contains(childJobs, j => j.EntityId == a2.Id && j.DesiredRevision == 10);
    }
}
