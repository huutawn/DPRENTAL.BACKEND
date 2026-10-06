using System.Text.Json;
using Dprental.InventoryService.Domain.Kafka.Contracts;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Domain.Clients.Catalog;
using Dprental.InventoryService.Domain.Clients.Media;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Domain.Kafka.Outbox;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class InventoryAssetServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    private sealed class FakeAssetRepository : IInventoryAssetRepository
    {
        public readonly Dictionary<Guid, InventoryAsset> Store = new();

        public Task<InventoryAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var a) ? a : null);

        public Task<InventoryAsset?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var a) && a.OwnerUserId == ownerUserId ? a : null);

        public Task<IReadOnlyList<InventoryAsset>> SearchAsync(AssetSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            var query = Store.Values.Where(a => a.OwnerUserId == criteria.OwnerUserId);

            if (!string.IsNullOrWhiteSpace(criteria.SearchKeyword))
            {
                query = query.Where(a => a.Model.Contains(criteria.SearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                                         (a.SerialNumber != null && a.SerialNumber.Contains(criteria.SearchKeyword, StringComparison.OrdinalIgnoreCase)));
            }

            if (criteria.EquipmentTypeId.HasValue)
            {
                query = query.Where(a => a.EquipmentTypeId == criteria.EquipmentTypeId.Value);
            }

            if (criteria.BrandId.HasValue)
            {
                query = query.Where(a => a.BrandId == criteria.BrandId.Value);
            }

            if (criteria.AcceptingBookings.HasValue)
            {
                query = query.Where(a => a.AcceptingBookings == criteria.AcceptingBookings.Value);
            }

            if (criteria.ManufactureYearFrom.HasValue)
            {
                query = query.Where(a => a.ManufactureYear.HasValue && a.ManufactureYear.Value >= criteria.ManufactureYearFrom.Value);
            }

            if (criteria.ManufactureYearTo.HasValue)
            {
                query = query.Where(a => a.ManufactureYear.HasValue && a.ManufactureYear.Value <= criteria.ManufactureYearTo.Value);
            }

            if (criteria.DynamicFilters != null && criteria.DynamicFilters.Count > 0)
            {
                if (criteria.CompatibleSchemaVersions == null || criteria.CompatibleSchemaVersions.Count == 0)
                {
                    return Task.FromResult<IReadOnlyList<InventoryAsset>>(Array.Empty<InventoryAsset>());
                }

                query = query.Where(a => criteria.CompatibleSchemaVersions.Contains(a.SchemaVersion));
            }

            var list = query
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)
                .Skip(criteria.Skip)
                .Take(criteria.Take)
                .ToList();

            return Task.FromResult<IReadOnlyList<InventoryAsset>>(list);
        }

        public Task<int> CountAsync(AssetSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            var query = Store.Values.Where(a => a.OwnerUserId == criteria.OwnerUserId);

            if (!string.IsNullOrWhiteSpace(criteria.SearchKeyword))
            {
                query = query.Where(a => a.Model.Contains(criteria.SearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                                         (a.SerialNumber != null && a.SerialNumber.Contains(criteria.SearchKeyword, StringComparison.OrdinalIgnoreCase)));
            }

            if (criteria.EquipmentTypeId.HasValue)
            {
                query = query.Where(a => a.EquipmentTypeId == criteria.EquipmentTypeId.Value);
            }

            if (criteria.BrandId.HasValue)
            {
                query = query.Where(a => a.BrandId == criteria.BrandId.Value);
            }

            if (criteria.AcceptingBookings.HasValue)
            {
                query = query.Where(a => a.AcceptingBookings == criteria.AcceptingBookings.Value);
            }

            if (criteria.ManufactureYearFrom.HasValue)
            {
                query = query.Where(a => a.ManufactureYear.HasValue && a.ManufactureYear.Value >= criteria.ManufactureYearFrom.Value);
            }

            if (criteria.ManufactureYearTo.HasValue)
            {
                query = query.Where(a => a.ManufactureYear.HasValue && a.ManufactureYear.Value <= criteria.ManufactureYearTo.Value);
            }

            if (criteria.DynamicFilters != null && criteria.DynamicFilters.Count > 0)
            {
                if (criteria.CompatibleSchemaVersions == null || criteria.CompatibleSchemaVersions.Count == 0)
                {
                    return Task.FromResult(0);
                }

                query = query.Where(a => criteria.CompatibleSchemaVersions.Contains(a.SchemaVersion));
            }

            return Task.FromResult(query.Count());
        }

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsAsync(AssetSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            var query = Store.Values.Where(a => a.OwnerUserId == criteria.OwnerUserId);

            if (criteria.EquipmentTypeId.HasValue)
            {
                query = query.Where(a => a.EquipmentTypeId == criteria.EquipmentTypeId.Value);
            }

            return Task.FromResult<IReadOnlyList<int>>(query.Select(a => a.SchemaVersion).Distinct().OrderBy(v => v).ToList());
        }

        public Task<InventoryAsset?> GetCalendarSnapshotAsync(Guid assetId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(assetId, out var a) ? a : null);

        public Task<IReadOnlyList<InventoryAsset>> GetAvailabilitySnapshotsAsync(IReadOnlyList<Guid> assetIds, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InventoryAsset>>(Store.Values.Where(a => assetIds.Contains(a.Id)).ToList());

        public void Add(InventoryAsset asset) => Store[asset.Id] = asset;
        public void Update(InventoryAsset asset) => Store[asset.Id] = asset;
    }

    private sealed class FakeWriteContext : IInventoryWriteContext
    {
        public Guid AssetId { get; }
        public IInventoryAssetRepository Assets { get; }
        public IAssetAllocationRepository Allocations => throw new NotImplementedException();
        public ICalendarBlockRepository CalendarBlocks => throw new NotImplementedException();
        public List<OutboxMessage> StagedOutbox { get; } = new();

        public FakeWriteContext(Guid assetId, IInventoryAssetRepository assets)
        {
            AssetId = assetId;
            Assets = assets;
        }

        public void StageOutbox(OutboxMessage message) => StagedOutbox.Add(message);
    }

    private sealed class FakeWriteTransaction : IInventoryWriteTransaction
    {
        private readonly FakeAssetRepository _repo;
        public readonly List<OutboxMessage> StagedOutbox = new();
        public Action? BeforeExecute { get; set; }
        public FakeWriteTransaction(FakeAssetRepository repo)
        {
            _repo = repo;
        }

        public async Task<Result<T>> ExecuteAsync<T>(
            Guid assetId,
            Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
            CancellationToken cancellationToken = default)
        {
            BeforeExecute?.Invoke();
            var ctx = new FakeWriteContext(assetId, _repo);
            var result = await operation(ctx, cancellationToken);
            if (result.IsSuccess)
            {
                StagedOutbox.AddRange(ctx.StagedOutbox);
            }
            return result;
        }
    }

    private sealed class FakeCatalogClient : ICatalogServiceClient
    {
        public Func<Guid, Task<Result<CatalogEquipmentTypeResponse>>>? LookupHandler { get; set; }
        public Func<Guid, Task<Result<CatalogBrandResponse>>>? BrandHandler { get; set; }
        public Func<Guid, int, string, Task<Result>>? SpecHandler { get; set; }
        public Func<Guid, IReadOnlyList<int>, Task<Result<CatalogBatchSchemasResponse>>>? BatchSchemasHandler { get; set; }
        public int LookupCalls { get; private set; }
        public int BrandCalls { get; private set; }
        public int SpecCalls { get; private set; }
        public int BatchSchemasCalls { get; private set; }

        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default)
        {
            LookupCalls++;
            return LookupHandler is not null
                ? LookupHandler(equipmentTypeId)
                : Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(
                    equipmentTypeId, "EQ-001", "Default Type", null, 1, DateTime.UtcNow, null, null)));
        }

        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid brandId, CancellationToken cancellationToken = default)
        {
            BrandCalls++;
            return BrandHandler is not null
                ? BrandHandler(brandId)
                : Task.FromResult(Result.Success(new CatalogBrandResponse(
                    brandId, "BR-001", "Default Brand", true)));
        }

        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default)
        {
            SpecCalls++;
            return SpecHandler is not null
                ? SpecHandler(equipmentTypeId, schemaVersion, specValues)
                : Task.FromResult(Result.Success());
        }

        public Task<Result<CatalogBatchSchemasResponse>> GetEquipmentTypeSchemasBatchAsync(Guid equipmentTypeId, IReadOnlyList<int> versions, CancellationToken cancellationToken = default)
        {
            BatchSchemasCalls++;
            return BatchSchemasHandler is not null
                ? BatchSchemasHandler(equipmentTypeId, versions)
                : Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
                    equipmentTypeId,
                    1,
                    [new CatalogBatchSchemaItemResponse(1, [])])));
        }
    }

    private sealed class FakeMediaClient : IMediaServiceClient
    {
        public Func<Guid, Task<Result<MediaVerificationInfo>>>? VerifyHandler { get; set; }
        public int VerifyCalls { get; private set; }

        public Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            VerifyCalls++;
            return VerifyHandler is not null
                ? VerifyHandler(mediaId)
                : Task.FromResult(Result.Success(new MediaVerificationInfo(mediaId, "Ready", Guid.NewGuid().ToString("D"))));
        }

        public Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(mediaId, "Public")));
    }

    private sealed class TestCaller(Guid? userId, bool isHuman = true, bool isAuthenticated = true, bool isAdmin = false, HashSet<string>? permissions = null) : IInventoryCallerContext
    {
        public bool IsAuthenticated => isAuthenticated;
        public bool IsService => !isHuman;
        public bool IsAdmin => isAdmin;
        public Guid? UserId => userId;
        public IReadOnlySet<string> Permissions => permissions ?? new HashSet<string> { InventoryAccessEvaluator.PermissionAssetManage };
        public IReadOnlySet<string> Scopes => new HashSet<string>();
        public IReadOnlySet<string> Roles => new HashSet<string>();
        public Guid? OrganizationId => null;
    }

    private readonly FakeAssetRepository _repo = new();
    private readonly FakeCatalogClient _catalogClient = new();
    private readonly FakeMediaClient _mediaClient = new();
    private readonly TestTimeProvider _clock = new(FixedNow);

    private InventoryAssetService CreateService()
    {
        var writeTx = new FakeWriteTransaction(_repo);
        var mediaOptions = Options.Create(new MediaServiceOptions
        {
            InternalBaseUrl = "http://media:8080",
            PublicGatewayUrl = "http://localhost:5050",
            ApiKey = "key",
            TimeoutSeconds = 10
        });
        var mediaService = new InventoryMediaService(_mediaClient, mediaOptions);

        return new InventoryAssetService(_repo, writeTx, _catalogClient, mediaService, _clock);
    }

    [Fact]
    public async Task CreateAssetAsync_ValidRequest_CreatesAssetAndReturns201Model()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        using var specDoc = JsonDocument.Parse("{\"sensor\":\"Full-Frame\"}");
        var request = new CreateInventoryAssetRequest(
            EquipmentTypeId: eqTypeId,
            BrandId: brandId,
            Model: "Sony FX3",
            SchemaVersion: 1,
            SpecValues: specDoc.RootElement.Clone(),
            SerialNumber: "SN-001",
            ManufactureYear: 2024);

        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsSuccess);
        var response = result.Value;
        Assert.Equal(ownerId, response.OwnerUserId);
        Assert.Equal(eqTypeId, response.EquipmentTypeId);
        Assert.Equal(brandId, response.BrandId);
        Assert.Equal("Sony FX3", response.Model);
        Assert.Equal("SN-001", response.SerialNumber);
        Assert.Equal(2024, response.ManufactureYear);
        Assert.True(response.AcceptingBookings);
        Assert.Null(response.SuspensionReason);
        Assert.Equal(FixedNow, response.CreatedAt);

        // Verify stored in repository
        Assert.True(_repo.Store.ContainsKey(response.Id));
        Assert.Equal(brandId, _repo.Store[response.Id].BrandId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task CreateAssetAsync_MissingOrEmptyBrandId_ReturnsValidation(string? brandGuidStr)
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());
        Guid? brandId = brandGuidStr is null ? null : Guid.Parse(brandGuidStr);

        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(
            Guid.NewGuid(), brandId, "FX3", 1, doc.RootElement);

        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_BRAND_ID", result.Error.Code);
        Assert.Equal(0, _catalogClient.BrandCalls);
    }

    [Fact]
    public async Task CreateAssetAsync_BrandNotFound_ReturnsValidationError()
    {
        var service = CreateService();
        var brandId = Guid.NewGuid();
        _catalogClient.BrandHandler = _ => Task.FromResult(Result.Failure<CatalogBrandResponse>(
            Error.Validation("Brand.NotFound", "Thương hiệu không tồn tại trong Catalog.")));

        var caller = new TestCaller(Guid.NewGuid());
        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(
            Guid.NewGuid(), brandId, "FX3", 1, doc.RootElement);

        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("Brand.NotFound", result.Error.Code);
        Assert.Empty(_repo.Store);
    }

    [Fact]
    public async Task CreateAssetAsync_BrandInactive_ReturnsValidationError()
    {
        var service = CreateService();
        var brandId = Guid.NewGuid();
        _catalogClient.BrandHandler = id => Task.FromResult(Result.Success(
            new CatalogBrandResponse(id, "BR-OLD", "Inactive Brand", false)));

        var caller = new TestCaller(Guid.NewGuid());
        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(
            Guid.NewGuid(), brandId, "FX3", 1, doc.RootElement);

        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("Brand.Inactive", result.Error.Code);
        Assert.Empty(_repo.Store);
    }

    [Fact]
    public async Task CreateAssetAsync_Unauthenticated_Returns401()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid(), isAuthenticated: false);

        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, doc.RootElement);

        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
        Assert.Equal(0, _catalogClient.LookupCalls);
    }

    [Fact]
    public async Task CreateAssetAsync_MissingManagePermission_Returns403()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid(), permissions: new HashSet<string>());

        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, doc.RootElement);

        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
        Assert.Equal(0, _catalogClient.LookupCalls);
    }

    [Fact]
    public async Task CreateAssetAsync_UnpublishedEquipmentType_ReturnsValidation()
    {
        var service = CreateService();
        _catalogClient.LookupHandler = _ => Task.FromResult(Result.Failure<CatalogEquipmentTypeResponse>(
            Error.Validation("EQUIPMENT_TYPE_NOT_PUBLISHED", "Loại thiết bị chưa được công khai.")));

        var caller = new TestCaller(Guid.NewGuid());
        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, doc.RootElement);

        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("EQUIPMENT_TYPE_NOT_PUBLISHED", result.Error.Code);
    }

    [Fact]
    public async Task CreateAssetAsync_CatalogSpecValidationFailure_RejectsCreation()
    {
        var service = CreateService();
        _catalogClient.SpecHandler = (_, _, _) => Task.FromResult(Result.Failure(
            Error.Validation("SPEC_VALIDATION_FAILED", "Dữ liệu specification không hợp lệ theo schema version đã công khai.",
                [new ValidationError("sensor", "REQUIRED", "Trường 'sensor' là bắt buộc.")])));

        var caller = new TestCaller(Guid.NewGuid());
        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, doc.RootElement);

        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("SPEC_VALIDATION_FAILED", result.Error.Code);
        Assert.Equal("Dữ liệu specification không hợp lệ theo schema version đã công khai.", result.Error.Message);
        Assert.NotNull(result.Error.Errors);
        var err = Assert.Single(result.Error.Errors);
        Assert.Equal("sensor", err.Field);
        Assert.Equal("REQUIRED", err.Code);
        Assert.Empty(_repo.Store);
    }

    [Fact]
    public async Task CreateAssetAsync_WithThumbnail_VerifiesAndPreparesPublicMedia()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);
        var thumbId = Guid.NewGuid();

        _mediaClient.VerifyHandler = id => Task.FromResult(Result.Success(new MediaVerificationInfo(id, "Ready", ownerId.ToString("D"))));

        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, doc.RootElement, ThumbnailMediaId: thumbId);

        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _mediaClient.VerifyCalls);
        Assert.Equal(thumbId, result.Value.ThumbnailMediaId);
    }

    [Fact]
    public async Task GetAssetByIdAsync_OwnershipIsolation_RejectsNonOwnerWith403()
    {
        var service = CreateService();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();

        var asset = InventoryAsset.Create(Guid.NewGuid(), ownerA, Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, "{}", FixedNow).Value;
        _repo.Add(asset);

        // Caller B tries to access asset of A
        var callerB = new TestCaller(ownerB);
        var result = await service.GetAssetByIdAsync(asset.Id, callerB);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
    }

    [Fact]
    public async Task GetAssetByIdAsync_AdminBypass_CanReadAsset()
    {
        var service = CreateService();
        var ownerA = Guid.NewGuid();
        var admin = new TestCaller(Guid.NewGuid(), isAdmin: true);

        var asset = InventoryAsset.Create(Guid.NewGuid(), ownerA, Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, "{}", FixedNow).Value;
        _repo.Add(asset);

        var result = await service.GetAssetByIdAsync(asset.Id, admin);

        Assert.True(result.IsSuccess);
        Assert.Equal(asset.Id, result.Value.Id);
    }

    [Fact]
    public async Task ListAssetsAsync_AlwaysFiltersByCallerUser_EvenForAdmin()
    {
        var service = CreateService();
        var ownerA = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        var assetA = InventoryAsset.Create(Guid.NewGuid(), ownerA, Guid.NewGuid(), Guid.NewGuid(), "FX3 A", 1, "{}", FixedNow).Value;
        var assetAdmin = InventoryAsset.Create(Guid.NewGuid(), adminId, Guid.NewGuid(), Guid.NewGuid(), "FX3 Admin", 1, "{}", FixedNow).Value;
        _repo.Add(assetA);
        _repo.Add(assetAdmin);

        var adminCaller = new TestCaller(adminId, isAdmin: true);
        var result = await service.ListAssetsAsync(new ListAssetsRequest(Page: 1, PageSize: 20), adminCaller);

        Assert.True(result.IsSuccess);
        // Only admin's asset returned, not assetA!
        Assert.Single(result.Value.Items);
        Assert.Equal(assetAdmin.Id, result.Value.Items[0].Id);
    }

    [Fact]
    public async Task PatchAssetAsync_PresenceTracking_UpdatesSpecifiedFieldsOnly()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var originalThumb = Guid.NewGuid();
        var originalBrand = Guid.NewGuid();
        var asset = InventoryAsset.Create(
            Guid.NewGuid(), ownerId, Guid.NewGuid(), originalBrand,
            "Original Model", 1, "{\"k\":\"v\"}", FixedNow,
            serialNumber: "SN-ORIG",
            manufactureYear: 2022,
            thumbnailMediaId: originalThumb).Value;
        _repo.Add(asset);

        var patch = new PatchInventoryAssetRequest
        {
            IsModelSpecified = true,
            Model = "Updated Model",
            IsSerialNumberSpecified = true,
            SerialNumber = null // Explicit null: clear serial!
            // brand omitted (keep originalBrand)
            // manufactureYear omitted (keep 2022)
            // thumbnail omitted (keep originalThumb)
            // schema/spec omitted (keep original)
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal("Updated Model", result.Value.Model);
        Assert.Equal(originalBrand, result.Value.BrandId);
        Assert.Null(result.Value.SerialNumber);
        Assert.Equal(2022, result.Value.ManufactureYear);
        Assert.Equal(originalThumb, result.Value.ThumbnailMediaId);
        Assert.Equal(0, _mediaClient.VerifyCalls); // No media call because thumbnail was not updated
        Assert.Equal(0, _catalogClient.BrandCalls); // Brand was not specified, no catalog call
        Assert.Equal(0, _catalogClient.SpecCalls); // Spec was not specified, no catalog call
    }

    [Fact]
    public async Task PatchAssetAsync_BrandUpdate_SucceedsWhenActive()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);
        var oldBrand = Guid.NewGuid();
        var newBrand = Guid.NewGuid();

        var asset = InventoryAsset.Create(
            Guid.NewGuid(), ownerId, Guid.NewGuid(), oldBrand,
            "FX3", 1, "{}", FixedNow).Value;
        _repo.Add(asset);

        var patch = new PatchInventoryAssetRequest
        {
            IsBrandIdSpecified = true,
            BrandId = newBrand
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(newBrand, result.Value.BrandId);
        Assert.Equal(1, _catalogClient.BrandCalls);
        Assert.Equal(0, _catalogClient.SpecCalls);
    }

    [Fact]
    public async Task PatchAssetAsync_BrandInactive_ReturnsValidationError()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);
        var inactiveBrand = Guid.NewGuid();

        _catalogClient.BrandHandler = id => Task.FromResult(Result.Success(
            new CatalogBrandResponse(id, "BR-INACTIVE", "Inactive Brand", false)));

        var asset = InventoryAsset.Create(
            Guid.NewGuid(), ownerId, Guid.NewGuid(), Guid.NewGuid(),
            "FX3", 1, "{}", FixedNow).Value;
        _repo.Add(asset);

        var patch = new PatchInventoryAssetRequest
        {
            IsBrandIdSpecified = true,
            BrandId = inactiveBrand
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("Brand.Inactive", result.Error.Code);
    }

    [Fact]
    public async Task PatchAssetAsync_CannotRemoveBrand_ReturnsValidationError()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = InventoryAsset.Create(
            Guid.NewGuid(), ownerId, Guid.NewGuid(), Guid.NewGuid(),
            "FX3", 1, "{}", FixedNow).Value;
        _repo.Add(asset);

        var patch = new PatchInventoryAssetRequest
        {
            IsBrandIdSpecified = true,
            BrandId = null
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("CANNOT_REMOVE_BRAND", result.Error.Code);
    }

    [Fact]
    public async Task PatchAssetAsync_EmptyGuidBrand_ReturnsValidationError()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = InventoryAsset.Create(
            Guid.NewGuid(), ownerId, Guid.NewGuid(), Guid.NewGuid(),
            "FX3", 1, "{}", FixedNow).Value;
        _repo.Add(asset);

        var patch = new PatchInventoryAssetRequest
        {
            IsBrandIdSpecified = true,
            BrandId = Guid.Empty
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_BRAND_ID", result.Error.Code);
    }

    [Fact]
    public async Task PatchAssetAsync_ModelOnly_DoesNotCallCatalog()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = InventoryAsset.Create(
            Guid.NewGuid(), ownerId, Guid.NewGuid(), Guid.NewGuid(),
            "FX3", 1, "{}", FixedNow).Value;
        _repo.Add(asset);

        var patch = new PatchInventoryAssetRequest
        {
            IsModelSpecified = true,
            Model = "FX3 Updated"
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, _catalogClient.LookupCalls);
        Assert.Equal(0, _catalogClient.BrandCalls);
        Assert.Equal(0, _catalogClient.SpecCalls);
    }

    [Fact]
    public async Task PatchAssetAsync_SpecOnly_ValidatesWithCurrentSchemaVersion()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = InventoryAsset.Create(
            Guid.NewGuid(), ownerId, Guid.NewGuid(), Guid.NewGuid(),
            "FX3", 2, "{\"color\":\"black\"}", FixedNow).Value;
        _repo.Add(asset);

        int validatedVersion = 0;
        string? validatedSpecs = null;
        _catalogClient.SpecHandler = (typeId, ver, specs) =>
        {
            validatedVersion = ver;
            validatedSpecs = specs;
            return Task.FromResult(Result.Success());
        };

        using var newSpecDoc = JsonDocument.Parse("{\"color\":\"silver\"}");
        var patch = new PatchInventoryAssetRequest
        {
            IsSpecValuesSpecified = true,
            SpecValues = newSpecDoc.RootElement.Clone()
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, validatedVersion); // Uses snapshot's schemaVersion = 2!
        Assert.Equal("{\"color\":\"silver\"}", validatedSpecs);
    }

    [Fact]
    public async Task PatchAssetAsync_SchemaOnly_ValidatesWithCurrentSpecValues()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = InventoryAsset.Create(
            Guid.NewGuid(), ownerId, Guid.NewGuid(), Guid.NewGuid(),
            "FX3", 1, "{\"resolution\":\"4K\"}", FixedNow).Value;
        _repo.Add(asset);

        int validatedVersion = 0;
        string? validatedSpecs = null;
        _catalogClient.SpecHandler = (typeId, ver, specs) =>
        {
            validatedVersion = ver;
            validatedSpecs = specs;
            return Task.FromResult(Result.Success());
        };

        var patch = new PatchInventoryAssetRequest
        {
            IsSchemaVersionSpecified = true,
            SchemaVersion = 3
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, validatedVersion); // Uses request's schemaVersion = 3!
        Assert.Equal("{\"resolution\":\"4K\"}", validatedSpecs); // Uses snapshot's specs!
    }

    [Fact]
    public async Task PatchAssetAsync_OldAssetNullBrand_PreservesNullWhenOmitted()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        // Simulate pre-migration asset with null BrandId using reflection
        var ctor = typeof(InventoryAsset).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .First(c => c.GetParameters().Length > 0);
        var oldAsset = (InventoryAsset)ctor.Invoke([
            Guid.NewGuid(), ownerId, Guid.NewGuid(), (Guid?)null, "Old Model", (string?)null, (int?)null, (Guid?)null, 1, "{}", true, (string?)null, FixedNow
        ]);
        _repo.Add(oldAsset);

        var patch = new PatchInventoryAssetRequest
        {
            IsModelSpecified = true,
            Model = "Updated Old Model"
        };

        var result = await service.PatchAssetAsync(oldAsset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.BrandId); // Preserves null!
        Assert.Equal("Updated Old Model", result.Value.Model);
    }

    [Fact]
    public async Task PatchAssetAsync_OldAssetNullBrand_CanBeBackfilledWithValidBrand()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);
        var brandId = Guid.NewGuid();

        var ctor = typeof(InventoryAsset).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .First(c => c.GetParameters().Length > 0);
        var oldAsset = (InventoryAsset)ctor.Invoke([
            Guid.NewGuid(), ownerId, Guid.NewGuid(), (Guid?)null, "Old Model", (string?)null, (int?)null, (Guid?)null, 1, "{}", true, (string?)null, FixedNow
        ]);
        _repo.Add(oldAsset);

        var patch = new PatchInventoryAssetRequest
        {
            IsBrandIdSpecified = true,
            BrandId = brandId
        };

        var result = await service.PatchAssetAsync(oldAsset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(brandId, result.Value.BrandId); // Successfully backfilled!
    }

    [Fact]
    public async Task PatchAssetAsync_EmptyPatch_Returns400()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());
        var emptyPatch = new PatchInventoryAssetRequest();

        var result = await service.PatchAssetAsync(Guid.NewGuid(), emptyPatch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("EMPTY_PATCH", result.Error.Code);
    }

    [Fact]
    public async Task PatchAssetAsync_ConcurrencyTokenConflict_Returns409()
    {
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = InventoryAsset.Create(Guid.NewGuid(), ownerId, Guid.NewGuid(), Guid.NewGuid(), "M", 1, "{}", FixedNow).Value;
        asset.Version = 100;
        _repo.Add(asset);

        var writeTx = new FakeWriteTransaction(_repo);
        // Simulate race: after snapshot read (outside tx), concurrent writer commits and bumps version to 101 before tx executes
        writeTx.BeforeExecute = () =>
        {
            asset.Version = 101;
        };

        var mediaOptions = Options.Create(new MediaServiceOptions { InternalBaseUrl = "http://m:80", PublicGatewayUrl = "http://m:50", ApiKey = "k", TimeoutSeconds = 10 });
        var mediaService = new InventoryMediaService(_mediaClient, mediaOptions);
        var service = new InventoryAssetService(_repo, writeTx, _catalogClient, mediaService, _clock);

        var patch = new PatchInventoryAssetRequest { IsModelSpecified = true, Model = "New M" };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal("CONCURRENCY_CONFLICT", result.Error.Code);
        Assert.Equal("M", _repo.Store[asset.Id].Model); // Original model untouched
    }

    [Fact]
    public async Task PatchAssetAsync_WithoutConcurrencyConflict_Succeeds()
    {
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = InventoryAsset.Create(Guid.NewGuid(), ownerId, Guid.NewGuid(), Guid.NewGuid(), "M", 1, "{}", FixedNow).Value;
        asset.Version = 100;
        _repo.Add(asset);

        var writeTx = new FakeWriteTransaction(_repo);
        var mediaOptions = Options.Create(new MediaServiceOptions { InternalBaseUrl = "http://m:80", PublicGatewayUrl = "http://m:50", ApiKey = "k", TimeoutSeconds = 10 });
        var mediaService = new InventoryMediaService(_mediaClient, mediaOptions);
        var service = new InventoryAssetService(_repo, writeTx, _catalogClient, mediaService, _clock);

        var patch = new PatchInventoryAssetRequest { IsModelSpecified = true, Model = "New M" };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal("New M", result.Value.Model);
        Assert.Equal("New M", _repo.Store[asset.Id].Model);
    }

    [Fact]
    public async Task ListAssetsAsync_PagingOffsetOverflow_Returns400()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await service.ListAssetsAsync(new ListAssetsRequest(Page: int.MaxValue, PageSize: 100), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_PAGE", result.Error.Code);
    }

    [Fact]
    public async Task PatchAssetAsync_SpecValidationInvalid_DoesNotCallMediaService()
    {
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);
        var newThumbId = Guid.NewGuid();

        var asset = InventoryAsset.Create(Guid.NewGuid(), ownerId, Guid.NewGuid(), Guid.NewGuid(), "M", 1, "{}", FixedNow).Value;
        _repo.Add(asset);

        _catalogClient.SpecHandler = (_, _, _) => Task.FromResult(Result.Failure(
            Error.Validation("SPEC_VALIDATION_FAILED", "Spec rejected by catalog",
                [new ValidationError("k", "REQUIRED", "Trường 'k' là bắt buộc.")])));

        var service = CreateService();

        using var specDoc = JsonDocument.Parse("{\"k\":\"new-v\"}");
        var patch = new PatchInventoryAssetRequest
        {
            IsSpecValuesSpecified = true,
            SpecValues = specDoc.RootElement.Clone(),
            IsThumbnailMediaIdSpecified = true,
            ThumbnailMediaId = newThumbId
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("SPEC_VALIDATION_FAILED", result.Error.Code);
        Assert.NotNull(result.Error.Errors);
        var patchErr = Assert.Single(result.Error.Errors);
        Assert.Equal("k", patchErr.Field);
        Assert.Equal("REQUIRED", patchErr.Code);
        Assert.Equal(0, _mediaClient.VerifyCalls); // Media preparation skipped when catalog spec fails
    }

    [Fact]
    public async Task ToggleAcceptingBookings_DisablesAndEmitsAvailabilityEvent_WhenFlagChanged()
    {
        var repo = new FakeAssetRepository();
        var writeTx = new FakeWriteTransaction(repo);
        var mediaOptions = Options.Create(new MediaServiceOptions { InternalBaseUrl = "http://m:80", PublicGatewayUrl = "http://m:50", ApiKey = "k", TimeoutSeconds = 10 });
        var mediaService = new InventoryMediaService(_mediaClient, mediaOptions);
        var service = new InventoryAssetService(repo, writeTx, _catalogClient, mediaService, _clock);

        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);
        var asset = InventoryAsset.Create(Guid.NewGuid(), ownerId, Guid.NewGuid(), Guid.NewGuid(), "M", 1, "{}", FixedNow).Value;
        repo.Add(asset);

        Assert.True(asset.AcceptingBookings);

        var toggleReq = new ToggleAcceptingBookingsRequest(AcceptingBookings: false, Reason: "Maintenance period");
        var result = await service.ToggleAcceptingBookingsAsync(asset.Id, toggleReq, caller);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.AcceptingBookings);
        Assert.Equal("Maintenance period", result.Value.SuspensionReason);

        // Outbox event emitted!
        Assert.Single(writeTx.StagedOutbox);
        var outbox = writeTx.StagedOutbox[0];
        Assert.Equal(KafkaTopics.InventoryAssetEventsV1, outbox.Topic);
        Assert.Equal(asset.Id.ToString("D"), outbox.KafkaKey);
        Assert.Contains(asset.Id.ToString("D"), outbox.Payload);

        // Toggle again to false (no change) -> no additional outbox event!
        var toggleAgain = await service.ToggleAcceptingBookingsAsync(asset.Id, toggleReq, caller);
        Assert.True(toggleAgain.IsSuccess);
        Assert.Single(writeTx.StagedOutbox); // Still 1!
    }

    [Fact]
    public async Task ListAssetsAsync_WithoutDynamicFilters_DoesNotCallCatalog()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await service.ListAssetsAsync(new ListAssetsRequest(), caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, _catalogClient.BatchSchemasCalls);
    }

    [Fact]
    public async Task ListAssetsAsync_EmptyFiltersStringOrArray_DoesNotCallCatalog()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var r1 = await service.ListAssetsAsync(new ListAssetsRequest(Filters: ""), caller);
        var r2 = await service.ListAssetsAsync(new ListAssetsRequest(Filters: "[]"), caller);

        Assert.True(r1.IsSuccess);
        Assert.True(r2.IsSuccess);
        Assert.Equal(0, _catalogClient.BatchSchemasCalls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not-a-json")]
    [InlineData("{\"field\":\"x\"}")] // Not an array
    public async Task ListAssetsAsync_MalformedFiltersJson_Returns400(string malformed)
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await service.ListAssetsAsync(new ListAssetsRequest(Filters: malformed), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task ListAssetsAsync_DynamicFiltersWithoutEquipmentTypeId_Returns400()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var filters = "[{\"field\":\"capacityKg\",\"operator\":\"gte\",\"value\":2500}]";
        var result = await service.ListAssetsAsync(new ListAssetsRequest(Filters: filters, EquipmentTypeId: null), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task ListAssetsAsync_DynamicFiltersExceeding20Conditions_Returns400()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var filterItems = Enumerable.Range(1, 21)
            .Select(i => $"{{\"field\":\"f{i}\",\"operator\":\"eq\",\"value\":\"val\"}}");
        var filters = $"[{string.Join(",", filterItems)}]";

        var result = await service.ListAssetsAsync(new ListAssetsRequest(Filters: filters, EquipmentTypeId: Guid.NewGuid()), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Theory]
    [InlineData("[{\"field\":\"c\",\"operator\":\"eq\",\"value\":1,\"extra\":true}]")] // Unknown property
    [InlineData("[{\"field\":\"c\",\"operator\":\"eq\"}]")] // Missing value
    [InlineData("[{\"operator\":\"eq\",\"value\":1}]")] // Missing field
    [InlineData("[{\"field\":\"\",\"operator\":\"eq\",\"value\":1}]")] // Empty field
    [InlineData("[{\"field\":\"c\",\"operator\":\"\",\"value\":1}]")] // Empty operator
    [InlineData("[{\"field\":\"c\",\"operator\":\"invalid_op\",\"value\":1}]")] // Disallowed operator
    public async Task ListAssetsAsync_InvalidFilterObjectStructure_Returns400(string badFilterJson)
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await service.ListAssetsAsync(new ListAssetsRequest(Filters: badFilterJson, EquipmentTypeId: Guid.NewGuid()), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task ListAssetsAsync_SearchKeywordExceeds200Chars_Returns400()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var longKeyword = new string('a', 201);
        var result = await service.ListAssetsAsync(new ListAssetsRequest(Q: longKeyword), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task ListAssetsAsync_EmptyUuid_Returns400()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var r1 = await service.ListAssetsAsync(new ListAssetsRequest(EquipmentTypeId: Guid.Empty), caller);
        var r2 = await service.ListAssetsAsync(new ListAssetsRequest(BrandId: Guid.Empty), caller);

        Assert.True(r1.IsFailure);
        Assert.True(r2.IsFailure);
    }

    [Theory]
    [InlineData(1899, 2024)]
    [InlineData(2024, 2050)]
    [InlineData(2025, 2020)] // From > To
    public async Task ListAssetsAsync_InvalidManufactureYears_Returns400(int from, int to)
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await service.ListAssetsAsync(new ListAssetsRequest(ManufactureYearFrom: from, ManufactureYearTo: to), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Theory]
    [InlineData("invalid_sort")]
    [InlineData("date_desc")]
    public async Task ListAssetsAsync_InvalidSortOption_Returns400(string sort)
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await service.ListAssetsAsync(new ListAssetsRequest(Sort: sort), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task ListAssetsAsync_FieldMissingOrNotFilterableInCurrentSchema_Returns400()
    {
        var eqId = Guid.NewGuid();
        _catalogClient.BatchSchemasHandler = (id, versions) => Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
            id,
            1,
            [
                new CatalogBatchSchemaItemResponse(1, [
                    new CatalogBatchFieldResponse("allowed", "Allowed", "text", true, false, false, 0, null, null, null, null, null) // filterable = false!
                ])
            ])));

        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        // 1. Missing field
        var r1 = await service.ListAssetsAsync(new ListAssetsRequest(EquipmentTypeId: eqId, Filters: "[{\"field\":\"missing\",\"operator\":\"eq\",\"value\":\"x\"}]"), caller);
        Assert.True(r1.IsFailure);
        Assert.Equal(ErrorType.Validation, r1.Error.Type);

        // 2. Field not filterable
        var r2 = await service.ListAssetsAsync(new ListAssetsRequest(EquipmentTypeId: eqId, Filters: "[{\"field\":\"allowed\",\"operator\":\"eq\",\"value\":\"x\"}]"), caller);
        Assert.True(r2.IsFailure);
        Assert.Equal(ErrorType.Validation, r2.Error.Type);
    }

    [Fact]
    public async Task ListAssetsAsync_EnumFilterWithInvalidOption_Returns400()
    {
        var eqId = Guid.NewGuid();
        _catalogClient.BatchSchemasHandler = (id, versions) => Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
            id,
            1,
            [
                new CatalogBatchSchemaItemResponse(1, [
                    new CatalogBatchFieldResponse("fuelType", "Fuel", "enum", true, true, false, 0, null, null,
                        [new CatalogBatchOptionResponse("diesel", "Diesel"), new CatalogBatchOptionResponse("electric", "Electric")],
                        null, null)
                ])
            ])));

        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await service.ListAssetsAsync(new ListAssetsRequest(EquipmentTypeId: eqId, Filters: "[{\"field\":\"fuelType\",\"operator\":\"eq\",\"value\":\"gasoline\"}]"), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task ListAssetsAsync_HistoricalVersionIncompatible_ExcludedFromSearch()
    {
        var ownerId = Guid.NewGuid();
        var eqId = Guid.NewGuid();

        // Asset 1 has schema_version 1 (incompatible: unit differs)
        var asset1 = InventoryAsset.Create(Guid.NewGuid(), ownerId, eqId, Guid.NewGuid(), "Model V1", 1, "{\"capacity\": 3000}", FixedNow).Value;
        // Asset 2 has schema_version 2 (compatible)
        var asset2 = InventoryAsset.Create(Guid.NewGuid(), ownerId, eqId, Guid.NewGuid(), "Model V2", 2, "{\"capacity\": 3000}", FixedNow).Value;
        _repo.Add(asset1);
        _repo.Add(asset2);

        _catalogClient.BatchSchemasHandler = (id, versions) => Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
            id,
            2,
            [
                new CatalogBatchSchemaItemResponse(1, [
                    new CatalogBatchFieldResponse("capacity", "Cap", "number", true, true, false, 0, 0, 10000, null, "lbs", null) // unit lbs!
                ]),
                new CatalogBatchSchemaItemResponse(2, [
                    new CatalogBatchFieldResponse("capacity", "Cap", "number", true, true, false, 0, 0, 10000, null, "kg", null) // unit kg!
                ])
            ])));

        var service = CreateService();
        var caller = new TestCaller(ownerId);

        var result = await service.ListAssetsAsync(new ListAssetsRequest(
            EquipmentTypeId: eqId,
            Filters: "[{\"field\":\"capacity\",\"operator\":\"gte\",\"value\":2500}]"), caller);

        Assert.True(result.IsSuccess);
        // Only asset2 matched because v1 was incompatible!
        Assert.Single(result.Value.Items);
        Assert.Equal(asset2.Id, result.Value.Items[0].Id);
    }

    [Theory]
    [InlineData("1e100")]
    [InlineData("-1e100")]
    [InlineData("1e309")]
    public async Task ListAssetsAsync_NumberFilter_DecimalOverflow_Returns400(string overflowNumber)
    {
        var eqId = Guid.NewGuid();
        _catalogClient.BatchSchemasHandler = (id, versions) => Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
            id,
            1,
            [
                new CatalogBatchSchemaItemResponse(1, [
                    new CatalogBatchFieldResponse("capacity", "Cap", "number", true, true, false, 0, null, null, null, null, null)
                ])
            ])));

        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var filterJson = $"[{{\"field\":\"capacity\",\"operator\":\"gte\",\"value\":{overflowNumber}}}]";
        var result = await service.ListAssetsAsync(new ListAssetsRequest(EquipmentTypeId: eqId, Filters: filterJson), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_QUERY_PARAMETER", result.Error.Code);
    }

    [Fact]
    public async Task ListAssetsAsync_NumberFilter_ValidDecimalBoundaries_Succeeds()
    {
        var eqId = Guid.NewGuid();
        _catalogClient.BatchSchemasHandler = (id, versions) => Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
            id,
            1,
            [
                new CatalogBatchSchemaItemResponse(1, [
                    new CatalogBatchFieldResponse("capacity", "Cap", "number", true, true, false, 0, null, null, null, null, null)
                ])
            ])));

        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        // decimal.MaxValue = 79228162514264337593543950335
        var filterJson = "[{\"field\":\"capacity\",\"operator\":\"lte\",\"value\":79228162514264337593543950335}]";
        var result = await service.ListAssetsAsync(new ListAssetsRequest(EquipmentTypeId: eqId, Filters: filterJson), caller);

        Assert.True(result.IsSuccess);
    }
}
