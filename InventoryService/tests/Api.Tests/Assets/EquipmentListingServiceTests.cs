using System.Text.Json;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Api.Tests.Allocations;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class EquipmentListingServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    private sealed class FakeListingRepository : IEquipmentListingRepository
    {
        public readonly Dictionary<Guid, EquipmentListing> Store = new();

        public Task<EquipmentListing?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) ? l : null);

        public Task<EquipmentListing?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) && l.OwnerUserId == ownerUserId ? l : null);

        public Task<EquipmentListing?> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) && l.Status == EquipmentListingStatus.Active ? l : null);
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
            Task.FromResult(Store.TryGetValue(listingId, out var l) ? l.Assets.Count(a => a.AcceptingBookings) : 0);

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsPublicAsync(AssetSearchFilters filters, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());

        public Task<PublicEquipmentListingSearchResult> SearchPublicAsync(EquipmentListingSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PublicEquipmentListingSearchResult(Array.Empty<PublicEquipmentListingItem>(), 0));

        public Task<EquipmentListing?> GetCalendarSnapshotAsync(Guid listingId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(listingId, out var l) ? l : null);

        public void Add(EquipmentListing listing) => Store[listing.Id] = listing;
        public void Update(EquipmentListing listing) => Store[listing.Id] = listing;
    }

    private sealed class FakeAssetRepository : IInventoryAssetRepository
    {
        public readonly Dictionary<Guid, InventoryAsset> Store = new();

        public Task<InventoryAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var a) ? a : null);

        public Task<InventoryAsset?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var a) && a.Listing?.OwnerUserId == ownerUserId ? a : null);

        public Task<IReadOnlyList<InventoryAsset>> SearchForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InventoryAsset>>(Store.Values.ToList());

        public Task<int> CountForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.Count);

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());

        public Task<IReadOnlyList<InventoryAsset>> GetAssetsByListingIdAsync(Guid listingId, int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InventoryAsset>>(Store.Values.Where(a => a.EquipmentListingId == listingId).Skip(skip).Take(take).ToList());

        public Task<int> CountAssetsByListingIdAsync(Guid listingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.Values.Count(a => a.EquipmentListingId == listingId));

        public Task<InventoryAsset?> GetCalendarSnapshotAsync(Guid assetId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(assetId, out var a) ? a : null);

        public Task<IReadOnlyList<InventoryAsset>> GetAvailabilitySnapshotsAsync(IReadOnlyList<Guid> assetIds, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InventoryAsset>>(Store.Values.Where(a => assetIds.Contains(a.Id)).ToList());

        public void Add(InventoryAsset asset) => Store[asset.Id] = asset;
        public Task<bool> ExistsSerialInListingAsync(Guid listingId, string serialNumber, Guid? excludeAssetId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.Values.Any(a => a.EquipmentListingId == listingId &&
                (!excludeAssetId.HasValue || a.Id != excludeAssetId.Value) &&
                string.Equals(a.SerialNumber.Trim(), serialNumber.Trim(), StringComparison.OrdinalIgnoreCase)));
        public void Update(InventoryAsset asset) => Store[asset.Id] = asset;
    }

    private sealed class FakeWriteContext : IInventoryWriteContext
    {
        public Guid? SearchBackfillRunId => null;
        public Guid AssetId => Guid.Empty;
        public Guid? ListingId { get; }
        public IEquipmentListingRepository Listings { get; }
        public IInventoryAssetRepository Assets { get; }
        public IAssetAllocationRepository Allocations => throw new NotImplementedException();
        public ICalendarBlockRepository CalendarBlocks => throw new NotImplementedException();

        public FakeWriteContext(Guid? listingId, IEquipmentListingRepository listings, IInventoryAssetRepository assets)
        {
            ListingId = listingId;
            Listings = listings;
            Assets = assets;
        }

        public Task AcquireAssetLocksAsync(IEnumerable<Guid> assetIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void StageOutbox(OutboxMessage message) { }
        public Task<long> GetNextSearchRevisionAsync(CancellationToken cancellationToken = default) => Task.FromResult(1L);
    }

    private sealed class FakeWriteTransaction : IInventoryWriteTransaction
    {
        private readonly FakeListingRepository _listings;
        private readonly FakeAssetRepository _assets;

        public FakeWriteTransaction(FakeListingRepository listings, FakeAssetRepository assets)
        {
            _listings = listings;
            _assets = assets;
        }

        public Task<Result<T>> ExecuteAsync<T>(Guid assetId, Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken = default) =>
            operation(new FakeWriteContext(null, _listings, _assets), cancellationToken);

        public Task<Result<T>> ExecuteListingWriteAsync<T>(Guid listingId, Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken = default) =>
            operation(new FakeWriteContext(listingId, _listings, _assets), cancellationToken);

        public Task<Result<T>> ExecuteGroupHoldAsync<T>(string sourceSystem, string groupReference, Guid listingId, Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken = default) =>
            operation(new FakeWriteContext(listingId, _listings, _assets), cancellationToken);
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

        public Task<Result<IReadOnlyList<Guid>>> GetEquipmentTypeIdsByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<Guid>>(Array.Empty<Guid>()));
    }

    private sealed class FakeMediaService : IInventoryMediaService
    {
        public Task<Result<IReadOnlyList<MediaReferenceResponse>>> PreparePublicAsync(
            IReadOnlyList<Guid> mediaIds,
            Guid? thumbnailMediaId,
            IInventoryCallerContext caller,
            IReadOnlyCollection<Guid>? existingMediaIds = null,
            Guid? existingThumbnailMediaId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<MediaReferenceResponse>>(
                mediaIds.Select(id => new MediaReferenceResponse(id, $"https://gateway.example/api/v1/media/{id:D}/content")).ToList()));
    }

    private readonly FakeListingRepository _listingRepo = new();
    private readonly FakeAssetRepository _assetRepo = new();
    private readonly FakeCatalogClient _catalogClient = new();
    private readonly FakeMediaService _mediaService = new();
    private readonly TestTimeProvider _clock = new(FixedNow);

    private EquipmentListingService CreateService()
    {
        var writeTx = new FakeWriteTransaction(_listingRepo, _assetRepo);
        var mediaOptions = Options.Create(new MediaServiceOptions { PublicGatewayUrl = "https://gateway.example" });
        return new EquipmentListingService(
            _listingRepo,
            _assetRepo,
            writeTx,
            _catalogClient,
            _mediaService,
            mediaOptions,
            _clock);
    }
    private static TestCaller CreateHumanCaller(Guid userId, params string[] permissions) =>
        new()
        {
            UserId = userId,
            IsService = false,
            IsAuthenticated = true,
            Permissions = new HashSet<string>(permissions)
        };

    [Fact]
    public async Task CreateListingAsync_ValidRequest_CreatesListingAndInitialUnit()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = CreateHumanCaller(ownerId, "inventory.asset.manage");

        using var specDoc = JsonDocument.Parse("{\"sensor\":\"Full-Frame\"}");
        var media1 = Guid.NewGuid();
        var media2 = Guid.NewGuid();
        var request = new CreateEquipmentListingRequest(
            EquipmentTypeId: Guid.NewGuid(),
            BrandId: Guid.NewGuid(),
            Name: "Sony FX3 kèm phụ kiện",
            Model: "Sony FX3",
            SerialNumber: "FX3-TEST-00001",
            SpecValues: specDoc.RootElement.Clone(),
            RentalRate: new RentalRateDto(500_000m, 3_000_000m, 10_000_000m, "VND"),
            ManufactureYear: 2024,
            Description: "Mô tả máy quay",
            SchemaVersion: 1,
            MediaIds: [media2, media1],
            ThumbnailMediaId: media1);

        var result = await service.CreateListingAsync(request, caller);

        Assert.True(result.IsSuccess);
        var response = result.Value;
        Assert.Equal(ownerId, response.OwnerUserId);
        Assert.Equal("Sony FX3", response.Model);
        Assert.Equal(2024, response.ManufactureYear);
        Assert.Equal(2, response.Media.Count);
        Assert.Equal(media2, response.Media[0].Id);
        Assert.Equal(media1, response.Media[1].Id);
        Assert.Equal(media1, response.ThumbnailMediaId);
        Assert.Equal("Draft", response.Status);
        Assert.Equal(1, response.TotalQuantity);

        // Verify stored in repositories and first asset inherited manufactureYear
        Assert.True(_listingRepo.Store.ContainsKey(response.Id));
        var assets = _assetRepo.Store.Values.Where(a => a.EquipmentListingId == response.Id).ToList();
        Assert.Single(assets);
        Assert.True(assets[0].AcceptingBookings);
        Assert.Equal(2024, assets[0].ManufactureYear);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateListingAsync_InvalidSerialNumber_ReturnsValidationError(string? invalidSerial)
    {
        var service = CreateService();
        var caller = CreateHumanCaller(Guid.NewGuid(), "inventory.asset.manage");

        using var specDoc = JsonDocument.Parse("{}");
        var request = new CreateEquipmentListingRequest(
            Guid.NewGuid(), Guid.NewGuid(), "Name", "Model", invalidSerial!, specDoc.RootElement,
            RentalRate: new RentalRateDto(100_000m, null, null, "VND"),
            ManufactureYear: 2024);
        var result = await service.CreateListingAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_SERIAL_NUMBER", result.Error.Code);
    }

    [Fact]
    public async Task AddAssetAsync_ValidRequest_AddsOneAssetAndReturnsResponse()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = CreateHumanCaller(ownerId, "inventory.asset.manage");

        var listingId = Guid.NewGuid();
        var listing = EquipmentListing.Create(
            listingId, ownerId, Guid.NewGuid(), Guid.NewGuid(), "FX3", null, "FX3", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2023).Value;
        _listingRepo.Add(listing);

        var addRequest = new AddListingAssetRequest("FX3-TEST-00002");
        var result = await service.AddAssetAsync(listingId, addRequest, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal("FX3-TEST-00002", result.Value.SerialNumber);
        Assert.Equal(2023, result.Value.ManufactureYear);
        Assert.Equal(1, _assetRepo.Store.Values.Count(a => a.EquipmentListingId == listingId));
    }

    [Fact]
    public async Task PatchListingAsync_ToActiveWithoutAcceptingUnits_Fails()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = CreateHumanCaller(ownerId);

        var listingId = Guid.NewGuid();
        var listing = EquipmentListing.Create(
            listingId, ownerId, Guid.NewGuid(), Guid.NewGuid(), "FX3", null, "FX3", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        _listingRepo.Add(listing);

        var patch = new PatchEquipmentListingRequest
        {
            IsStatusSpecified = true,
            Status = EquipmentListingStatus.Active
        };

        var result = await service.PatchListingAsync(listingId, patch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("LISTING_NEEDS_ACTIVE_UNIT", result.Error.Code);
    }

    [Fact]
    public async Task UpdateRentalRateAsync_ValidRates_UpdatesRates()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = CreateHumanCaller(ownerId);

        var listingId = Guid.NewGuid();
        var listing = EquipmentListing.Create(
            listingId, ownerId, Guid.NewGuid(), Guid.NewGuid(), "FX3", null, "FX3", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        _listingRepo.Add(listing);

        var newRates = new RentalRateDto(200_000m, 1_200_000m, 4_000_000m, "VND");
        var result = await service.UpdateRentalRateAsync(listingId, newRates, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(200_000m, result.Value.DailyRate);
        Assert.Equal(1_200_000m, result.Value.WeeklyRate);
        Assert.Equal(4_000_000m, result.Value.MonthlyRate);
    }
    [Fact]
    public async Task ServicePrincipal_IsForbidden_OnAllListingServiceMethods()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var serviceCaller = new TestCaller { UserId = ownerId, IsService = true, Permissions = new HashSet<string> { "inventory.asset.manage" } };

        var listingId = Guid.NewGuid();
        var listing = EquipmentListing.Create(
            listingId, ownerId, Guid.NewGuid(), Guid.NewGuid(), "FX3", null, "FX3", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        _listingRepo.Add(listing);

        using var specDoc = JsonDocument.Parse("{}");
        var createReq = new CreateEquipmentListingRequest(
            Guid.NewGuid(), Guid.NewGuid(), "Name", "Model", "SN-1", specDoc.RootElement, new RentalRateDto(100_000m, null, null, "VND"), 2024);

        var rCreate = await service.CreateListingAsync(createReq, serviceCaller);
        Assert.Equal("FORBIDDEN", rCreate.Error.Code);

        var rGet = await service.GetListingByIdAsync(listingId, serviceCaller);
        Assert.Equal("FORBIDDEN", rGet.Error.Code);

        var rList = await service.ListOwnerListingsAsync(new ListEquipmentListingsRequest(), serviceCaller);
        Assert.Equal("FORBIDDEN", rList.Error.Code);

        var rPatch = await service.PatchListingAsync(listingId, new PatchEquipmentListingRequest { Model = "New" }, serviceCaller);
        Assert.Equal("FORBIDDEN", rPatch.Error.Code);

        var rRate = await service.UpdateRentalRateAsync(listingId, new RentalRateDto(200_000m, null, null, "VND"), serviceCaller);
        Assert.Equal("FORBIDDEN", rRate.Error.Code);

        var rAdd = await service.AddAssetAsync(listingId, new AddListingAssetRequest("SN-2"), serviceCaller);
        Assert.Equal("FORBIDDEN", rAdd.Error.Code);

        var rAssets = await service.GetListingAssetsAsync(listingId, 1, 20, serviceCaller);
        Assert.Equal("FORBIDDEN", rAssets.Error.Code);
    }

    [Fact]
    public async Task PatchListingManufactureYear_OnlyAffectsSubsequentAssets_AndLegacyNullYearBlocksAddAssetUntilPatched()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = CreateHumanCaller(ownerId, "inventory.asset.manage");

        var listingId = Guid.NewGuid();
        var listing = EquipmentListing.Create(
            listingId, ownerId, Guid.NewGuid(), Guid.NewGuid(), "FX3", null, "FX3", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2021).Value;
        // Simulate legacy listing with null ManufactureYear
        typeof(EquipmentListing).GetProperty(nameof(EquipmentListing.ManufactureYear))!.SetValue(listing, null);
        _listingRepo.Add(listing);

        // 1. Add asset when listing ManufactureYear is null -> rejected
        var addBlocked = await service.AddAssetAsync(listingId, new AddListingAssetRequest("SN-BLOCKED"), caller);
        Assert.True(addBlocked.IsFailure);
        Assert.Equal("INVALID_MANUFACTURE_YEAR", addBlocked.Error.Code);

        // 2. Patch listing with valid ManufactureYear = 2022
        var patchYear = await service.PatchListingAsync(listingId, new PatchEquipmentListingRequest
        {
            IsManufactureYearSpecified = true,
            ManufactureYear = 2022
        }, caller);
        Assert.True(patchYear.IsSuccess);
        Assert.Equal(2022, patchYear.Value.ManufactureYear);

        // 3. Add asset 1 -> inherits 2022
        var asset1 = await service.AddAssetAsync(listingId, new AddListingAssetRequest("SN-001"), caller);
        Assert.True(asset1.IsSuccess);
        Assert.Equal(2022, asset1.Value.ManufactureYear);

        // 4. Patch listing ManufactureYear = 2025 -> asset 1 remains 2022, new asset 2 gets 2025
        var patchYear2 = await service.PatchListingAsync(listingId, new PatchEquipmentListingRequest
        {
            IsManufactureYearSpecified = true,
            ManufactureYear = 2025
        }, caller);
        Assert.True(patchYear2.IsSuccess);
        Assert.Equal(2022, _assetRepo.Store[asset1.Value.Id].ManufactureYear);

        var asset2 = await service.AddAssetAsync(listingId, new AddListingAssetRequest("SN-002"), caller);
        Assert.True(asset2.IsSuccess);
        Assert.Equal(2025, asset2.Value.ManufactureYear);
    }

    [Fact]
    public async Task PatchListingMedia_EnforcesOmittedNullEmptyAndThumbnailCoherence()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = CreateHumanCaller(ownerId, "inventory.asset.manage");
        var m1 = Guid.NewGuid();
        var m2 = Guid.NewGuid();

        var listingId = Guid.NewGuid();
        var listing = EquipmentListing.Create(
            listingId, ownerId, Guid.NewGuid(), Guid.NewGuid(), "FX3", null, "FX3", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024, [m1, m2], m1).Value;
        _listingRepo.Add(listing);

        // 1. mediaIds: null is rejected
        var resNullMedia = await service.PatchListingAsync(listingId, new PatchEquipmentListingRequest
        {
            IsMediaIdsSpecified = true,
            MediaIds = null
        }, caller);
        Assert.True(resNullMedia.IsFailure);
        Assert.Equal("INVALID_MEDIA_IDS", resNullMedia.Error.Code);

        // 2. Removing m1 (current thumbnail) without clearing or changing thumbnail is rejected
        var resRemoveThumbWithoutClearing = await service.PatchListingAsync(listingId, new PatchEquipmentListingRequest
        {
            IsMediaIdsSpecified = true,
            MediaIds = [m2]
        }, caller);
        Assert.True(resRemoveThumbWithoutClearing.IsFailure);
        Assert.Equal("INVALID_THUMBNAIL_MEDIA_ID", resRemoveThumbWithoutClearing.Error.Code);

        // 3. Removing m1 while selecting m2 as thumbnail succeeds
        var resSelectNewThumb = await service.PatchListingAsync(listingId, new PatchEquipmentListingRequest
        {
            IsMediaIdsSpecified = true,
            MediaIds = [m2],
            IsThumbnailMediaIdSpecified = true,
            ThumbnailMediaId = m2
        }, caller);
        Assert.True(resSelectNewThumb.IsSuccess);
        Assert.Single(resSelectNewThumb.Value.Media);
        Assert.Equal(m2, resSelectNewThumb.Value.ThumbnailMediaId);

        // 4. Clearing mediaIds: [] along with thumbnailMediaId: null succeeds
        var resClearAll = await service.PatchListingAsync(listingId, new PatchEquipmentListingRequest
        {
            IsMediaIdsSpecified = true,
            MediaIds = [],
            IsThumbnailMediaIdSpecified = true,
            ThumbnailMediaId = null
        }, caller);
        Assert.True(resClearAll.IsSuccess);
        Assert.Empty(resClearAll.Value.Media);
        Assert.Null(resClearAll.Value.ThumbnailMediaId);
        Assert.Null(resClearAll.Value.ThumbnailUrl);
    }

    [Fact]
    public async Task OwnerStatistics_ServiceToken_Returns403Forbidden()
    {
        var service = CreateService();
        var caller = new TestCaller { IsService = true };

        var result = await service.GetOwnerStatisticsAsync(null, caller, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("SERVICE_TOKEN_REJECTED", result.Error.Code);
    }

    [Fact]
    public async Task OwnerStatistics_Unauthenticated_Returns401Unauthorized()
    {
        var service = CreateService();
        var caller = new TestCaller { IsAuthenticated = false, UserId = null };

        var result = await service.GetOwnerStatisticsAsync(null, caller, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_REQUIRED", result.Error.Code);
    }

    [Fact]
    public async Task OwnerStatistics_Admin_CountsOnlyTheirOwnListings()
    {
        var adminId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        var adminListing = EquipmentListing.Create(
            Guid.NewGuid(), adminId, typeId, brandId, "Admin Listing", null, "Model A", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        _listingRepo.Add(adminListing);

        var otherListing = EquipmentListing.Create(
            Guid.NewGuid(), otherUserId, typeId, brandId, "Other Listing", null, "Model B", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        _listingRepo.Add(otherListing);

        var service = CreateService();
        var adminCaller = new TestCaller { IsAdmin = true, UserId = adminId, IsAuthenticated = true, IsService = false };

        var result = await service.GetOwnerStatisticsAsync(null, adminCaller, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.ByEquipmentType[typeId]);
        Assert.Equal(1, result.Value.ByBrand[brandId]);
    }

    [Fact]
    public async Task OwnerStatistics_WithStatusFilter_FiltersByStatus()
    {
        var userId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        var draftListing = EquipmentListing.Create(
            Guid.NewGuid(), userId, typeId, brandId, "Draft Listing", null, "Model A", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        _listingRepo.Add(draftListing);

        var activeListing = EquipmentListing.Create(
            Guid.NewGuid(), userId, typeId, brandId, "Active Listing", null, "Model B", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        activeListing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        _listingRepo.Add(activeListing);

        var service = CreateService();
        var caller = CreateHumanCaller(userId);

        var activeResult = await service.GetOwnerStatisticsAsync(EquipmentListingStatus.Active, caller, CancellationToken.None);
        Assert.True(activeResult.IsSuccess);
        Assert.Equal(1, activeResult.Value.ByEquipmentType[typeId]);

        var draftResult = await service.GetOwnerStatisticsAsync(EquipmentListingStatus.Draft, caller, CancellationToken.None);
        Assert.True(draftResult.IsSuccess);
        Assert.Equal(1, draftResult.Value.ByEquipmentType[typeId]);

        var allResult = await service.GetOwnerStatisticsAsync(null, caller, CancellationToken.None);
        Assert.True(allResult.IsSuccess);
        Assert.Equal(2, allResult.Value.ByEquipmentType[typeId]);
    }

    [Fact]
    public async Task OwnerStatistics_InvalidStatus_Returns400Validation()
    {
        var service = CreateService();
        var caller = CreateHumanCaller(Guid.NewGuid());

        var result = await service.GetOwnerStatisticsAsync((EquipmentListingStatus)999, caller, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_QUERY_PARAMETER", result.Error.Code);
    }
}
