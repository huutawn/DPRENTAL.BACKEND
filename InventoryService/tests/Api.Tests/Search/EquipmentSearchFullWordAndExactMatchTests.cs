using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Api.Tests.Integration;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Search;
using Dprental.InventoryService.Application.Search.Adapters;
using Dprental.InventoryService.Application.Search.Configuration;
using Dprental.InventoryService.Application.Search.Models;
using Dprental.InventoryService.Application.Search.Mapping;
using Dprental.InventoryService.Application.Search.Workers;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Meilisearch;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Search;

public sealed class EquipmentSearchFullWordAndExactMatchTests : IAsyncLifetime, IAsyncDisposable
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

    private Guid _owner1Id;
    private Guid _owner2Id;
    private Guid _typeId;
    private Guid _brandId;

    private EquipmentListing _listingXeNang = null!;
    private EquipmentListing _listingXeTai = null!;
    private EquipmentListing _listingBanNang = null!;
    private EquipmentListing _listingXeLuAbc = null!;
    private EquipmentListing _listingXeLuAbcLon = null!;
    private EquipmentListing _listingMayUi = null!;

    private InventoryAsset _assetXeNang = null!;
    private InventoryAsset _assetXeLuAbc = null!;

    public EquipmentSearchFullWordAndExactMatchTests()
    {
        _options = new MeilisearchOptions
        {
            Url = MeiliUrl,
            ApiKey = MasterKey,
            PublicListingsIndex = $"test_pub_fullword_{Guid.NewGuid():N}",
            OwnerListingsIndex = $"test_own_fullword_{Guid.NewGuid():N}",
            OwnerAssetsIndex = $"test_ast_fullword_{Guid.NewGuid():N}",
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
        Assert.True(await _client.IsHealthyAsync(), "Meilisearch must be healthy for this test suite.");
        await _postgres.InitializeAsync();
        _db = new InventoryServiceDbContext(new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_postgres.ConnectionString).Options);
        _store = new SearchProjectionStore(_db);

        var generation = SearchGeneration.CreateInitial(
            _options.PublicListingsIndex,
            _options.OwnerListingsIndex,
            _options.OwnerAssetsIndex,
            DateTimeOffset.UtcNow);
        generation.MarkReady(DateTimeOffset.UtcNow);
        await _store.SaveGenerationAsync(generation);

        _searchService = new MeilisearchInventoryIndexService(
            _client,
            _store,
            Microsoft.Extensions.Options.Options.Create(_options),
            NullLogger<MeilisearchInventoryIndexService>.Instance);

        await _provisioner.EnsureIndexesAndSettingsAsync();

        await SeedDatasetAsync();
    }

    public async Task DisposeAsync()
    {
        try { await _client.DeleteIndexAsync(_options.PublicListingsIndex); } catch { }
        try { await _client.DeleteIndexAsync(_options.OwnerListingsIndex); } catch { }
        try { await _client.DeleteIndexAsync(_options.OwnerAssetsIndex); } catch { }
        if (_db != null) await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    private async Task SeedDatasetAsync()
    {
        var now = DateTimeOffset.UtcNow;
        _owner1Id = Guid.NewGuid();
        _owner2Id = Guid.NewGuid();
        _typeId = Guid.NewGuid();
        _brandId = Guid.NewGuid();

        _db.Warehouses.Add(Warehouse.CreateWithAddress(_owner1Id, "123 Street", "01", "Thành phố Hà Nội", "00070", "Phường Hoàn Kiếm", 21.0, 105.8, now).Value);
        _db.Warehouses.Add(Warehouse.CreateWithAddress(_owner2Id, "456 Street", "01", "Thành phố Hà Nội", "00070", "Phường Hoàn Kiếm", 21.0, 105.8, now).Value);

        // 1. "Xe nâng"
        _listingXeNang = EquipmentListing.Create(
            Guid.NewGuid(), _owner1Id, _typeId, _brandId,
            "Xe nâng", "Mô tả thiết bị nâng hàng trong kho bãi", "XN-01",
            1, "{}", 500000, null, null, "VND", now, 2024).Value;
        _listingXeNang.UpdateStatus(EquipmentListingStatus.Active, true, now);
        _db.EquipmentListings.Add(_listingXeNang);

        // 2. "Xe tải"
        _listingXeTai = EquipmentListing.Create(
            Guid.NewGuid(), _owner1Id, _typeId, _brandId,
            "Xe tải", "Mô tả xe tải vận chuyển hàng hóa", "XT-01",
            1, "{}", 600000, null, null, "VND", now, 2024).Value;
        _listingXeTai.UpdateStatus(EquipmentListingStatus.Active, true, now);
        _db.EquipmentListings.Add(_listingXeTai);

        // 3. "Bàn nâng"
        _listingBanNang = EquipmentListing.Create(
            Guid.NewGuid(), _owner1Id, _typeId, _brandId,
            "Bàn nâng", "Mô tả bàn nâng thủy lực công nghiệp", "BN-01",
            1, "{}", 400000, null, null, "VND", now, 2024).Value;
        _listingBanNang.UpdateStatus(EquipmentListingStatus.Active, true, now);
        _db.EquipmentListings.Add(_listingBanNang);

        // 4. "Xe lu ABC"
        _listingXeLuAbc = EquipmentListing.Create(
            Guid.NewGuid(), _owner1Id, _typeId, _brandId,
            "Xe lu ABC", "Mô tả xe lu đường", "ABC-1",
            1, "{}", 700000, null, null, "VND", now, 2024).Value;
        _listingXeLuAbc.UpdateStatus(EquipmentListingStatus.Active, true, now);
        _db.EquipmentListings.Add(_listingXeLuAbc);

        // 5. "Xe lu ABC công suất lớn"
        _listingXeLuAbcLon = EquipmentListing.Create(
            Guid.NewGuid(), _owner1Id, _typeId, _brandId,
            "Xe lu ABC công suất lớn", "Mô tả xe lu công trình lớn", "ABC-2",
            1, "{}", 800000, null, null, "VND", now, 2024).Value;
        _listingXeLuAbcLon.UpdateStatus(EquipmentListingStatus.Active, true, now);
        _db.EquipmentListings.Add(_listingXeLuAbcLon);

        // 6. "Máy ủi đất" (only contains keywords in description!)
        _listingMayUi = EquipmentListing.Create(
            Guid.NewGuid(), _owner1Id, _typeId, _brandId,
            "Máy ủi đất", "Thiết bị có chức năng xe nâng và xe lu ABC hỗ trợ san lấp", "MU-01",
            1, "{}", 900000, null, null, "VND", now, 2024).Value;
        _listingMayUi.UpdateStatus(EquipmentListingStatus.Active, true, now);
        _db.EquipmentListings.Add(_listingMayUi);

        // Assets for Xe nâng and Xe lu ABC
        _assetXeNang = InventoryAsset.Create(Guid.NewGuid(), _listingXeNang.Id, "SN-XENANG-01", now, 2024).Value;
        _assetXeLuAbc = InventoryAsset.Create(Guid.NewGuid(), _listingXeLuAbc.Id, "SN-XELU-ABC-01", now, 2024).Value;
        _db.Assets.Add(_assetXeNang);
        _db.Assets.Add(_assetXeLuAbc);

        await _db.SaveChangesAsync();

        // Index all documents into Meilisearch
        var publicIndex = _client.Index(_options.PublicListingsIndex);
        var ownerIndex = _client.Index(_options.OwnerListingsIndex);
        var assetIndex = _client.Index(_options.OwnerAssetsIndex);

        var allListings = new[] { _listingXeNang, _listingXeTai, _listingBanNang, _listingXeLuAbc, _listingXeLuAbcLon, _listingMayUi };
        var pubDocs = allListings.Select(SearchProjectionMapper.ToPublicListingDocument).ToList();
        var ownDocs = allListings.Select(SearchProjectionMapper.ToOwnerListingDocument).ToList();

        var loadedAssets = await _db.Assets.Include(a => a.Listing).ToListAsync();
        var astDocs = loadedAssets.Select(SearchProjectionMapper.ToOwnerAssetDocument).ToList();

        var t1 = await publicIndex.AddDocumentsAsync(pubDocs, "id");
        var t2 = await ownerIndex.AddDocumentsAsync(ownDocs, "id");
        var t3 = await assetIndex.AddDocumentsAsync(astDocs, "id");

        await _client.WaitForTaskAsync(t1.TaskUid, timeoutMs: 15000);
        await _client.WaitForTaskAsync(t2.TaskUid, timeoutMs: 15000);
        await _client.WaitForTaskAsync(t3.TaskUid, timeoutMs: 15000);
    }

    [Fact]
    public async Task Search_XeNang_RequiresBothWords_ExcludesSingleWordAndDescriptionOnly()
    {
        // 1. Public Listings: Meilisearch
        var pubResult = await _searchService.SearchPublicListingCandidatesAsync(
            new PublicListingSearchIndexQuery("xe nâng"));
        Assert.True(pubResult.IsSuccess);
        Assert.Contains(_listingXeNang.Id, pubResult.Value.CandidateIds);
        Assert.DoesNotContain(_listingXeTai.Id, pubResult.Value.CandidateIds);
        Assert.DoesNotContain(_listingBanNang.Id, pubResult.Value.CandidateIds);
        Assert.DoesNotContain(_listingMayUi.Id, pubResult.Value.CandidateIds);

        // 2. Owner Listings: Meilisearch
        var ownResult = await _searchService.SearchOwnerListingCandidatesAsync(
            new OwnerListingSearchIndexQuery(_owner1Id, "xe nâng"));
        Assert.True(ownResult.IsSuccess);
        Assert.Contains(_listingXeNang.Id, ownResult.Value.CandidateIds);
        Assert.DoesNotContain(_listingXeTai.Id, ownResult.Value.CandidateIds);
        Assert.DoesNotContain(_listingBanNang.Id, ownResult.Value.CandidateIds);
        Assert.DoesNotContain(_listingMayUi.Id, ownResult.Value.CandidateIds);

        // 3. Owner Assets: Meilisearch
        var astResult = await _searchService.SearchOwnerAssetCandidatesAsync(
            new OwnerAssetSearchIndexQuery(_owner1Id, "xe nâng"));
        Assert.True(astResult.IsSuccess);
        Assert.Contains(_assetXeNang.Id, astResult.Value.CandidateIds);
        Assert.DoesNotContain(_assetXeLuAbc.Id, astResult.Value.CandidateIds);
    }

    [Fact]
    public async Task Search_XeLuAbc_PrioritizesExactNameMatch()
    {
        // Public Listing Search via Repository with Meilisearch candidates
        var pubCandidates = await _searchService.SearchPublicListingCandidatesAsync(
            new PublicListingSearchIndexQuery("xe lu ABC"));
        Assert.True(pubCandidates.IsSuccess);
        Assert.Contains(_listingXeLuAbc.Id, pubCandidates.Value.CandidateIds);
        Assert.Contains(_listingXeLuAbcLon.Id, pubCandidates.Value.CandidateIds);

        var repo = new EquipmentListingRepository(_db);
        var criteria = new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(
                SearchKeyword: "xe lu ABC",
                Sort: AssetSortOption.Relevance,
                CandidateIds: pubCandidates.Value.CandidateIds),
            availabilityFrom: null,
            availabilityTo: null,
            requestedQuantity: null,
            availabilityEvaluationUtc: DateTimeOffset.UtcNow,
            skip: 0,
            take: 10);

        var searchResult = await repo.SearchPublicAsync(criteria);
        Assert.True(searchResult.TotalCount >= 2);
        // Exact match "Xe lu ABC" MUST be first!
        Assert.Equal(_listingXeLuAbc.Id, searchResult.Items[0].Listing.Id);
        Assert.Equal(_listingXeLuAbcLon.Id, searchResult.Items[1].Listing.Id);
    }

    [Fact]
    public async Task Search_Diacritics_Casing_Whitespace_AndDecomposedUnicode()
    {
        // 1. Unaccented "xe nang" matches "Xe nâng" on Meilisearch
        var resUnaccented = await _searchService.SearchPublicListingCandidatesAsync(
            new PublicListingSearchIndexQuery("xe nang"));
        Assert.True(resUnaccented.IsSuccess);
        Assert.Contains(_listingXeNang.Id, resUnaccented.Value.CandidateIds);
        Assert.DoesNotContain(_listingXeTai.Id, resUnaccented.Value.CandidateIds);

        // 2. Extra whitespace & mixed case: "   xE     NÂNG   "
        var resWhitespace = await _searchService.SearchPublicListingCandidatesAsync(
            new PublicListingSearchIndexQuery("   xE     NÂNG   "));
        Assert.True(resWhitespace.IsSuccess);
        Assert.Contains(_listingXeNang.Id, resWhitespace.Value.CandidateIds);

        // 3. Decomposed Unicode (NFD)
        string nfdQuery = "xe nâng".Normalize(NormalizationForm.FormD);
        var resNfd = await _searchService.SearchPublicListingCandidatesAsync(
            new PublicListingSearchIndexQuery(nfdQuery));
        Assert.True(resNfd.IsSuccess);
        Assert.Contains(_listingXeNang.Id, resNfd.Value.CandidateIds);
    }

    [Fact]
    public async Task Search_OwnerAssets_SerialTypoDisabled_ListingNameAndModelSearchable()
    {
        // 1. Search asset by Listing Name "xe nâng"
        var resByName = await _searchService.SearchOwnerAssetCandidatesAsync(
            new OwnerAssetSearchIndexQuery(_owner1Id, "xe nâng"));
        Assert.True(resByName.IsSuccess);
        Assert.Contains(_assetXeNang.Id, resByName.Value.CandidateIds);

        // 2. Search asset by Model "XN-01"
        var resByModel = await _searchService.SearchOwnerAssetCandidatesAsync(
            new OwnerAssetSearchIndexQuery(_owner1Id, "XN-01"));
        Assert.True(resByModel.IsSuccess);
        Assert.Contains(_assetXeNang.Id, resByModel.Value.CandidateIds);

        // 3. Search asset by exact Serial "SN-XENANG-01"
        var resBySerial = await _searchService.SearchOwnerAssetCandidatesAsync(
            new OwnerAssetSearchIndexQuery(_owner1Id, "SN-XENANG-01"));
        Assert.True(resBySerial.IsSuccess);
        Assert.Contains(_assetXeNang.Id, resBySerial.Value.CandidateIds);

        // 4. Serial typo (01 -> 02) MUST NOT match
        var resTypo = await _searchService.SearchOwnerAssetCandidatesAsync(
            new OwnerAssetSearchIndexQuery(_owner1Id, "SN-XENANG-02"));
        Assert.True(resTypo.IsSuccess);
        Assert.Empty(resTypo.Value.CandidateIds);
    }

    [Fact]
    public async Task SqlFallback_RequiresAllWords_AppliesPriorityRanking()
    {
        var repo = new EquipmentListingRepository(_db);

        // 1. SQL Fallback for "xe nâng" (CandidateIds = null)
        var criteriaXeNang = new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(
                SearchKeyword: "xe nâng",
                Sort: AssetSortOption.Relevance,
                CandidateIds: null),
            availabilityFrom: null,
            availabilityTo: null,
            requestedQuantity: null,
            availabilityEvaluationUtc: DateTimeOffset.UtcNow,
            skip: 0,
            take: 10);

        var resXeNang = await repo.SearchPublicAsync(criteriaXeNang);
        Assert.Equal(1, resXeNang.TotalCount);
        Assert.Equal(_listingXeNang.Id, resXeNang.Items[0].Listing.Id);

        // 2. SQL Fallback for "xe lu ABC"
        var criteriaXeLu = new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(
                SearchKeyword: "xe lu ABC",
                Sort: AssetSortOption.Relevance,
                CandidateIds: null),
            availabilityFrom: null,
            availabilityTo: null,
            requestedQuantity: null,
            availabilityEvaluationUtc: DateTimeOffset.UtcNow,
            skip: 0,
            take: 10);

        var resXeLu = await repo.SearchPublicAsync(criteriaXeLu);
        Assert.Equal(2, resXeLu.TotalCount);
        // Exact match "Xe lu ABC" MUST be #1
        Assert.Equal(_listingXeLuAbc.Id, resXeLu.Items[0].Listing.Id);
        Assert.Equal(_listingXeLuAbcLon.Id, resXeLu.Items[1].Listing.Id);

        // 3. Owner Assets SQL Fallback
        var assetRepo = new InventoryAssetRepository(_db);
        var assetCriteria = new OwnerAssetSearchCriteria(
            ownerUserId: _owner1Id,
            filters: new AssetSearchFilters(
                SearchKeyword: "xe nâng",
                Sort: AssetSortOption.Relevance,
                CandidateIds: null),
            acceptingBookings: null,
            skip: 0,
            take: 10);

        var astItems = await assetRepo.SearchForOwnerAsync(assetCriteria);
        var astCount = await assetRepo.CountForOwnerAsync(assetCriteria);
        Assert.Equal(1, astCount);
        Assert.Single(astItems);
        Assert.Equal(_assetXeNang.Id, astItems[0].Id);
    }

    [Fact]
    public async Task HttpSearch_AllThreeFlows_OwnerIsolation_Filters_And_Pagination()
    {
        await using var fixture = new InventorySecurityTestFactory(_postgres.ConnectionString);
        await using var app = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IInventorySearchIndexService>(sp => new MeilisearchInventoryIndexService(
                _client, sp.GetRequiredService<ISearchProjectionStore>(), Options.Create(_options),
                NullLogger<MeilisearchInventoryIndexService>.Instance))));
        using var client = app.CreateClient();

        // 1. Flow 1: Public Listings HTTP
        using var pubResponse = await client.GetAsync("/api/v1/inventory/public/equipment-listings?q=xe%20n%C3%A2ng");
        Assert.Equal(HttpStatusCode.OK, pubResponse.StatusCode);
        Assert.Equal("meilisearch", Assert.Single(pubResponse.Headers.GetValues("X-Search-Backend")));
        using var pubJson = JsonDocument.Parse(await pubResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, pubJson.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(_listingXeNang.Id, pubJson.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());

        // 2. Flow 2: Owner Listings HTTP (Owner 1)
        string owner1Token = fixture.CreateHumanToken(_owner1Id, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner1Token);

        using var ownResponse = await client.GetAsync("/api/v1/inventory/equipment-listings?q=xe%20lu%20ABC");
        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        using var ownJson = JsonDocument.Parse(await ownResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, ownJson.RootElement.GetProperty("totalCount").GetInt32());
        // Exact match first
        Assert.Equal(_listingXeLuAbc.Id, ownJson.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(_listingXeLuAbcLon.Id, ownJson.RootElement.GetProperty("items")[1].GetProperty("id").GetGuid());

        // Owner 2 Isolation: Owner 2 has no listings
        string owner2Token = fixture.CreateHumanToken(_owner2Id, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner2Token);
        using var own2Response = await client.GetAsync("/api/v1/inventory/equipment-listings?q=xe%20lu%20ABC");
        Assert.Equal(HttpStatusCode.OK, own2Response.StatusCode);
        using var own2Json = JsonDocument.Parse(await own2Response.Content.ReadAsStringAsync());
        Assert.Equal(0, own2Json.RootElement.GetProperty("totalCount").GetInt32());

        // 3. Flow 3: Owner Assets HTTP (Owner 1 searches by listing name)
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner1Token);
        using var astResponse = await client.GetAsync("/api/v1/inventory/assets?q=xe%20n%C3%A2ng");
        var astContent = await astResponse.Content.ReadAsStringAsync();
        Assert.True(astResponse.StatusCode == HttpStatusCode.OK, $"Expected OK but got {astResponse.StatusCode}: {astContent}");
        using var astJson = JsonDocument.Parse(astContent);
        Assert.Equal(1, astJson.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(_assetXeNang.Id, astJson.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        // Explicit sort check
        using var sortResponse = await client.GetAsync("/api/v1/inventory/public/equipment-listings?q=xe&sort=model_asc");
        Assert.Equal(HttpStatusCode.OK, sortResponse.StatusCode);
        using var sortJson = JsonDocument.Parse(await sortResponse.Content.ReadAsStringAsync());
        var items = sortJson.RootElement.GetProperty("items");
        if (items.GetArrayLength() >= 2)
        {
            string m0 = items[0].GetProperty("model").GetString()!;
            string m1 = items[1].GetProperty("model").GetString()!;
            Assert.True(string.Compare(m0, m1, StringComparison.Ordinal) <= 0);
        }
    }

    [Fact]
    public async Task Backfill_PopulatesNameOnAllOwnerAssetDocuments()
    {
        var services = new ServiceCollection();
        services.AddDbContext<InventoryServiceDbContext>(o => o.UseNpgsql(_postgres.ConnectionString));
        services.AddScoped<ISearchProjectionStore, SearchProjectionStore>();
        services.AddSingleton<IInventoryIndexProvisioner>(_provisioner);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var backfillService = new MeilisearchBackfillService(
            scopeFactory,
            _client,
            Microsoft.Extensions.Options.Options.Create(_options),
            TimeProvider.System,
            NullLogger<MeilisearchBackfillService>.Instance);

        var result = await backfillService.BackfillAsync(CancellationToken.None);
        Assert.True(result.OwnerAssetsCount >= 2);

        // Verify OwnerAssetIndexDocument has Name populated
        var assetIndex = _client.Index(_options.OwnerAssetsIndex);
        var doc = await assetIndex.GetDocumentAsync<OwnerAssetIndexDocument>(_assetXeNang.Id.ToString("D"));
        Assert.NotNull(doc);
        Assert.Equal("Xe nâng", doc.Name);
        Assert.Equal("XN-01", doc.Model);
        Assert.Equal("SN-XENANG-01", doc.SerialNumber);
    }
}
