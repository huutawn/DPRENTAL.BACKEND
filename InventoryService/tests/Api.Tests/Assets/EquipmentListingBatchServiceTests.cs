using Dprental.InventoryService.Api.Tests.Allocations;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class EquipmentListingBatchServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

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
        public Task<Result<Dprental.InventoryService.Application.Caching.CachedRead<IReadOnlyList<Guid>>>> GetEquipmentTypeIdsByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<IReadOnlyList<Guid>>(Array.Empty<Guid>(), DateTimeOffset.UtcNow.AddMinutes(5))));
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
            Task.FromResult(Result.Success<IReadOnlyList<MediaReferenceResponse>>(Array.Empty<MediaReferenceResponse>()));
    }

    private readonly FakeListingRepository _listingRepo = new();
    private readonly FakeAssetRepository _assetRepo = new();
    private sealed class FakeBatchWriteTransaction : IInventoryWriteTransaction
    {
        public Task<Result<T>> ExecuteAsync<T>(Guid assetId, Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<Result<T>> ExecuteListingWriteAsync<T>(Guid listingId, Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<Result<T>> ExecuteGroupHoldAsync<T>(string sourceSystem, string groupReference, Guid listingId, Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<Result<T>> ExecuteAllocationCommandAsync<T>(string sourceSystem, string operationKey, string groupReference, Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }


    private EquipmentListingService CreateService()
    {
        var mediaOptions = Options.Create(new MediaServiceOptions { PublicGatewayUrl = "https://gateway.example" });
        return new EquipmentListingService(
            _listingRepo,
            _assetRepo,
            new FakeBatchWriteTransaction(),
            new FakeCatalogClient(),
            new FakeMediaService(),
            mediaOptions,
            new TestTimeProvider(FixedNow));
    }

    private static TestCaller CreateServiceCaller(params string[] scopes) =>
        new()
        {
            IsAuthenticated = true,
            IsService = true,
            Scopes = new HashSet<string>(scopes)
        };

    private static TestCaller CreateHumanCaller(bool isAdmin = false) =>
        new()
        {
            IsAuthenticated = true,
            IsService = false,
            IsAdmin = isAdmin,
            UserId = Guid.NewGuid(),
            Roles = isAdmin ? new HashSet<string> { "adm" } : new HashSet<string>()
        };

    private EquipmentListing AddListing(
        Guid? id = null,
        Guid? ownerUserId = null,
        string name = "Sony FX3",
        decimal dailyRate = 500_000m,
        string currency = "VND",
        EquipmentListingStatus status = EquipmentListingStatus.Active,
        Guid? thumbnailMediaId = null,
        decimal depositAmount = 500_000m)
    {
        var mediaIds = thumbnailMediaId.HasValue ? new[] { thumbnailMediaId.Value } : null;
        var listing = EquipmentListing.Create(
            id ?? Guid.NewGuid(),
            ownerUserId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            name,
            "Description should be ignored",
            "FX3",
            1,
            "{\"sensor\":\"Full-Frame\"}",
            dailyRate,
            null,
            null,
            currency,
            FixedNow,
            2024,
            mediaIds: mediaIds,
            thumbnailMediaId: thumbnailMediaId,
            depositAmount: depositAmount).Value;

        listing.UpdateStatus(status, true, FixedNow);
        _listingRepo.Store[listing.Id] = listing;
        return listing;
    }

    [Fact]
    public async Task GetListingBatchAsync_NullRequest_ReturnsValidation()
    {
        var service = CreateService();
        var caller = CreateServiceCaller(InventoryAccessEvaluator.ScopeAvailabilityRead);

        var result = await service.GetListingBatchAsync(null!, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_REQUEST", result.Error.Code);
    }

    [Theory]
    [InlineData(null)]
    public async Task GetListingBatchAsync_NullIds_ReturnsValidation(IReadOnlyList<Guid>? ids)
    {
        var service = CreateService();
        var caller = CreateServiceCaller(InventoryAccessEvaluator.ScopeAvailabilityRead);

        var result = await service.GetListingBatchAsync(new EquipmentListingBatchRequest(ids), caller);

        Assert.True(result.IsFailure);
        Assert.Equal("EMPTY_EQUIPMENT_LISTING_IDS", result.Error.Code);
    }

    [Fact]
    public async Task GetListingBatchAsync_EmptyIds_ReturnsValidation()
    {
        var service = CreateService();
        var caller = CreateServiceCaller(InventoryAccessEvaluator.ScopeAvailabilityRead);

        var result = await service.GetListingBatchAsync(new EquipmentListingBatchRequest(Array.Empty<Guid>()), caller);

        Assert.True(result.IsFailure);
        Assert.Equal("EMPTY_EQUIPMENT_LISTING_IDS", result.Error.Code);
    }

    [Fact]
    public async Task GetListingBatchAsync_Exceeds100Ids_ReturnsValidation()
    {
        var service = CreateService();
        var caller = CreateServiceCaller(InventoryAccessEvaluator.ScopeAvailabilityRead);
        var ids = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToList();

        var result = await service.GetListingBatchAsync(new EquipmentListingBatchRequest(ids), caller);

        Assert.True(result.IsFailure);
        Assert.Equal("EXCEEDED_EQUIPMENT_LISTING_IDS_LIMIT", result.Error.Code);
    }

    [Fact]
    public async Task GetListingBatchAsync_ContainsEmptyGuid_ReturnsValidation()
    {
        var service = CreateService();
        var caller = CreateServiceCaller(InventoryAccessEvaluator.ScopeAvailabilityRead);
        var ids = new[] { Guid.NewGuid(), Guid.Empty, Guid.NewGuid() };

        var result = await service.GetListingBatchAsync(new EquipmentListingBatchRequest(ids), caller);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_EQUIPMENT_LISTING_ID", result.Error.Code);
    }

    [Fact]
    public async Task GetListingBatchAsync_UnauthenticatedCaller_ReturnsUnauthorized()
    {
        var service = CreateService();
        var caller = new TestCaller { IsAuthenticated = false };

        var result = await service.GetListingBatchAsync(
            new EquipmentListingBatchRequest([Guid.NewGuid()]),
            caller);

        Assert.True(result.IsFailure);
        Assert.Equal("UNAUTHORIZED", result.Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetListingBatchAsync_HumanCaller_ReturnsForbidden(bool isAdmin)
    {
        var service = CreateService();
        var caller = CreateHumanCaller(isAdmin);

        var result = await service.GetListingBatchAsync(
            new EquipmentListingBatchRequest([Guid.NewGuid()]),
            caller);

        Assert.True(result.IsFailure);
        Assert.Equal("FORBIDDEN", result.Error.Code);
    }


    [Fact]
    public async Task GetListingBatchAsync_DeduplicatesIds_AndMaintainsFirstSeenOrder()
    {
        var service = CreateService();
        var caller = CreateServiceCaller(InventoryAccessEvaluator.ScopeAvailabilityRead);

        var l1 = AddListing(name: "Listing 1");
        var l2 = AddListing(name: "Listing 2");
        var l3 = AddListing(name: "Listing 3");

        // Request with duplicates: l2, l1, l2, l3, l1
        var requestIds = new[] { l2.Id, l1.Id, l2.Id, l3.Id, l1.Id };
        var result = await service.GetListingBatchAsync(new EquipmentListingBatchRequest(requestIds), caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.Items.Count);
        Assert.Empty(result.Value.MissingIds);

        // First seen order: l2, l1, l3
        Assert.Equal(l2.Id, result.Value.Items[0].Id);
        Assert.Equal(l1.Id, result.Value.Items[1].Id);
        Assert.Equal(l3.Id, result.Value.Items[2].Id);
    }

    [Fact]
    public async Task GetListingBatchAsync_MixedBatch_ReturnsFoundAndMissingInCorrectOrder()
    {
        var service = CreateService();
        var caller = CreateServiceCaller(InventoryAccessEvaluator.ScopeAvailabilityRead);

        var l1 = AddListing(name: "Listing 1");
        var missing1 = Guid.NewGuid();
        var l2 = AddListing(name: "Listing 2");
        var missing2 = Guid.NewGuid();

        var requestIds = new[] { missing1, l1.Id, missing2, l2.Id, missing1 };
        var result = await service.GetListingBatchAsync(new EquipmentListingBatchRequest(requestIds), caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Items.Count);
        Assert.Equal(l1.Id, result.Value.Items[0].Id);
        Assert.Equal(l2.Id, result.Value.Items[1].Id);

        Assert.Equal(2, result.Value.MissingIds.Count);
        Assert.Equal(missing1, result.Value.MissingIds[0]);
        Assert.Equal(missing2, result.Value.MissingIds[1]);
    }

    [Fact]
    public async Task GetListingBatchAsync_AllMissing_Returns200WithEmptyItemsAndAllMissingIds()
    {
        var service = CreateService();
        var caller = CreateServiceCaller(InventoryAccessEvaluator.ScopeAvailabilityRead);

        var m1 = Guid.NewGuid();
        var m2 = Guid.NewGuid();

        var result = await service.GetListingBatchAsync(new EquipmentListingBatchRequest([m1, m2, m1]), caller);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Items);
        Assert.Equal(2, result.Value.MissingIds.Count);
        Assert.Equal(m1, result.Value.MissingIds[0]);
        Assert.Equal(m2, result.Value.MissingIds[1]);
    }

    [Fact]
    public async Task GetListingBatchAsync_ReturnsAllStatuses_IncludingDraftAndInactive()
    {
        var service = CreateService();
        var caller = CreateServiceCaller(InventoryAccessEvaluator.ScopeAvailabilityRead);

        var draft = AddListing(name: "Draft Listing", status: EquipmentListingStatus.Draft);
        var active = AddListing(name: "Active Listing", status: EquipmentListingStatus.Active);
        var inactive = AddListing(name: "Inactive Listing", status: EquipmentListingStatus.Inactive);

        var result = await service.GetListingBatchAsync(
            new EquipmentListingBatchRequest([draft.Id, active.Id, inactive.Id]),
            caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.Items.Count);
        Assert.Equal("Draft", result.Value.Items[0].Status);
        Assert.Equal("Active", result.Value.Items[1].Status);
        Assert.Equal("Inactive", result.Value.Items[2].Status);
    }

    [Fact]
    public async Task GetListingBatchAsync_ReadsAcrossMultipleSuppliers()
    {
        var service = CreateService();
        var caller = CreateServiceCaller(InventoryAccessEvaluator.ScopeAvailabilityRead);

        var supplier1 = Guid.NewGuid();
        var supplier2 = Guid.NewGuid();

        var l1 = AddListing(ownerUserId: supplier1, name: "Supplier 1 Listing");
        var l2 = AddListing(ownerUserId: supplier2, name: "Supplier 2 Listing");

        var result = await service.GetListingBatchAsync(
            new EquipmentListingBatchRequest([l1.Id, l2.Id]),
            caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Items.Count);
        Assert.Equal(supplier1, result.Value.Items[0].OwnerUserId);
        Assert.Equal(supplier2, result.Value.Items[1].OwnerUserId);
    }

    [Fact]
    public async Task GetListingBatchAsync_ItemResponseOnlyExposesMinimalContractFields()
    {
        var service = CreateService();
        var caller = CreateServiceCaller(InventoryAccessEvaluator.ScopeAvailabilityRead);

        var thumbnailId = Guid.NewGuid();
        var listing = AddListing(
            name: "Sony FX3",
            dailyRate: 500_000m,
            currency: "VND",
            status: EquipmentListingStatus.Active,
            thumbnailMediaId: thumbnailId);

        var result = await service.GetListingBatchAsync(
            new EquipmentListingBatchRequest([listing.Id]),
            caller);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value.Items);

        Assert.Equal(listing.Id, item.Id);
        Assert.Equal(listing.OwnerUserId, item.OwnerUserId);
        Assert.Equal("Sony FX3", item.Name);
        Assert.Equal(thumbnailId, item.ThumbnailMediaId);
        Assert.Equal(500_000m, item.DailyRate);
        Assert.Equal("VND", item.Currency);
        Assert.Equal("Active", item.Status);
        Assert.Equal(500_000m, item.DepositAmount);
    }


    [Fact]
    public async Task GetListingBatchAsync_IncludeWarehouse_WithWarehouseReadScope_ReturnsWarehouseDetails()
    {
        var service = CreateService();
        var caller = CreateServiceCaller(
            InventoryAccessEvaluator.ScopeAvailabilityRead,
            InventoryAccessEvaluator.ScopeWarehouseRead);

        var ownerId = Guid.NewGuid();
        var listing = AddListing(ownerUserId: ownerId);
        var warehouse = Warehouse.CreateWithAddress(
            ownerId, "123 Le Loi", "79", "Thành phố Hồ Chí Minh", "25747", "Phường Thủ Dầu Một", 10.7, 106.7, FixedNow).Value;
        // Wire warehouse navigation
        typeof(EquipmentListing).GetProperty(nameof(EquipmentListing.Warehouse))!.SetValue(listing, warehouse);

        var request = new EquipmentListingBatchRequest([listing.Id], IncludeWarehouse: true);

        var result = await service.GetListingBatchAsync(request, caller);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value.Items);
        Assert.NotNull(item.Warehouse);
        Assert.Equal("123 Le Loi", item.Warehouse.AddressDetail);
        Assert.Equal("79", item.Warehouse.ProvinceCode);
        Assert.Equal("Thành phố Hồ Chí Minh", item.Warehouse.ProvinceName);
        Assert.Equal("25747", item.Warehouse.WardCode);
        Assert.Equal("Phường Thủ Dầu Một", item.Warehouse.WardName);
        Assert.Equal(10.7, item.Warehouse.Latitude);
        Assert.Equal(106.7, item.Warehouse.Longitude);
    }

    [Fact]
    public async Task GetListingBatchAsync_DefaultIncludeWarehouseFalse_WarehouseDetailsAreNull()
    {
        var service = CreateService();
        var caller = CreateServiceCaller(
            InventoryAccessEvaluator.ScopeAvailabilityRead,
            InventoryAccessEvaluator.ScopeWarehouseRead);

        var ownerId = Guid.NewGuid();
        var listing = AddListing(ownerUserId: ownerId);
        var warehouse = Warehouse.CreateWithAddress(
            ownerId, "123 Le Loi", "79", "Thành phố Hồ Chí Minh", "25747", "Phường Thủ Dầu Một", 10.7, 106.7, FixedNow).Value;
        typeof(EquipmentListing).GetProperty(nameof(EquipmentListing.Warehouse))!.SetValue(listing, warehouse);

        // IncludeWarehouse defaults to false
        var request = new EquipmentListingBatchRequest([listing.Id]);

        var result = await service.GetListingBatchAsync(request, caller);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value.Items);
        Assert.Null(item.Warehouse);
    }
}
