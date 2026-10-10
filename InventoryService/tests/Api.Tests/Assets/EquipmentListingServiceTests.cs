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

        public Task<IReadOnlyList<EquipmentListing>> ListForOwnerAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EquipmentListing>>(Store.Values.Where(l => l.OwnerUserId == ownerUserId).Skip(skip).Take(take).ToList());

        public Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.Values.Count(l => l.OwnerUserId == ownerUserId));

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

        public Task<(IReadOnlyList<InventoryAsset> Items, int TotalCount)> GetFilteredAssetsByListingIdAsync(
            Guid listingId,
            DateOnly startDate,
            DateOnly endDate,
            string status,
            DateTimeOffset evaluationNowUtc,
            int skip,
            int take,
            CancellationToken cancellationToken = default)
        {
            var units = Store.Values.Where(a => a.EquipmentListingId == listingId);
            Func<InventoryAsset, bool> predicate = status switch
            {
                "available" => a => a.AcceptingBookings &&
                    !a.CalendarBlocks.Any(cb => cb.StartDate <= endDate && cb.EndDate >= startDate) &&
                    !a.Allocations.Any(al =>
                        (al.Status == AllocationStatus.Allocated ||
                         (al.Status == AllocationStatus.Held && al.HoldExpiresAt != null && al.HoldExpiresAt > evaluationNowUtc)) &&
                        al.RentalPeriod.StartDate <= endDate &&
                        al.RentalPeriod.EndDate >= startDate),
                "rented" => a => a.Allocations.Any(al =>
                    al.Status == AllocationStatus.Allocated &&
                    al.RentalPeriod.StartDate <= endDate &&
                    al.RentalPeriod.EndDate >= startDate),
                "held" => a => a.Allocations.Any(al =>
                    al.Status == AllocationStatus.Held &&
                    al.HoldExpiresAt != null && al.HoldExpiresAt > evaluationNowUtc &&
                    al.RentalPeriod.StartDate <= endDate &&
                    al.RentalPeriod.EndDate >= startDate),
                "blocked" => a => a.CalendarBlocks.Any(cb =>
                    cb.StartDate <= endDate &&
                    cb.EndDate >= startDate),
                _ => _ => false
            };

            var filtered = units.Where(predicate).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToList();
            int count = filtered.Count;
            var paged = filtered.Skip(skip).Take(take).ToList();
            return Task.FromResult<(IReadOnlyList<InventoryAsset> Items, int TotalCount)>((paged, count));
        }
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
    }

    private sealed class FakeMediaService : IInventoryMediaService
    {
        public Task<Result<MediaReferenceResponse?>> PreparePublicAsync(Guid? mediaId, IInventoryCallerContext caller, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<MediaReferenceResponse?>(mediaId.HasValue ? new MediaReferenceResponse(mediaId.Value, "https://gateway.example/content") : null));
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
        var request = new CreateEquipmentListingRequest(
            EquipmentTypeId: Guid.NewGuid(),
            BrandId: Guid.NewGuid(),
            Model: "Sony FX3",
            SerialNumber: "FX3-TEST-00001",
            SpecValues: specDoc.RootElement.Clone(),
            RentalRate: new RentalRateDto(500_000m, 3_000_000m, 10_000_000m, "VND"),
            SchemaVersion: 1);

        var result = await service.CreateListingAsync(request, caller);

        Assert.True(result.IsSuccess);
        var response = result.Value;
        Assert.Equal(ownerId, response.OwnerUserId);
        Assert.Equal("Sony FX3", response.Model);
        Assert.Equal("Draft", response.Status);
        Assert.Equal(1, response.TotalQuantity);

        // Verify stored in repositories
        Assert.True(_listingRepo.Store.ContainsKey(response.Id));
        Assert.Equal(1, _assetRepo.Store.Values.Count(a => a.EquipmentListingId == response.Id));
        Assert.All(_assetRepo.Store.Values.Where(a => a.EquipmentListingId == response.Id), a => Assert.True(a.AcceptingBookings));
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
            Guid.NewGuid(), Guid.NewGuid(), "Model", invalidSerial!, specDoc.RootElement,
            RentalRate: new RentalRateDto(100_000m, null, null, "VND"));

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
            listingId, ownerId, Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, "{}", 100_000m, null, null, "VND", FixedNow).Value;
        _listingRepo.Add(listing);

        var addRequest = new AddListingAssetRequest("FX3-TEST-00002");
        var result = await service.AddAssetAsync(listingId, addRequest, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal("FX3-TEST-00002", result.Value.SerialNumber);
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
            listingId, ownerId, Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, "{}", 100_000m, null, null, "VND", FixedNow).Value;
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
            listingId, ownerId, Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, "{}", 100_000m, null, null, "VND", FixedNow).Value;
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
            listingId, ownerId, Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, "{}", 100_000m, null, null, "VND", FixedNow).Value;
        _listingRepo.Add(listing);

        using var specDoc = JsonDocument.Parse("{}");
        var createReq = new CreateEquipmentListingRequest(
            Guid.NewGuid(), Guid.NewGuid(), "Model", "SN-1", specDoc.RootElement, new RentalRateDto(100_000m, null, null, "VND"));

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
}
