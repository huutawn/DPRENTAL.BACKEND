using System.Text.Json;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed partial class InventoryAssetServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    private sealed class FakeAssetRepository : IInventoryAssetRepository
    {
        public readonly Dictionary<Guid, InventoryAsset> Store = new();

        public Task<InventoryAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var a) ? a : null);

        public Task<InventoryAsset?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var a) && a.Listing?.OwnerUserId == ownerUserId ? a : null);

        public Task<IReadOnlyList<InventoryAsset>> SearchForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            var query = Store.Values.Where(a => a.Listing?.OwnerUserId == criteria.OwnerUserId);
            var filters = criteria.Filters;

            if (!string.IsNullOrWhiteSpace(filters.SearchKeyword))
            {
                query = query.Where(a => (a.Listing != null && a.Listing.Model.Contains(filters.SearchKeyword, StringComparison.OrdinalIgnoreCase)) ||
                                         (a.SerialNumber != null && a.SerialNumber.Contains(filters.SearchKeyword, StringComparison.OrdinalIgnoreCase)));
            }

            if (filters.EquipmentTypeId.HasValue)
            {
                query = query.Where(a => a.Listing != null && a.Listing.EquipmentTypeId == filters.EquipmentTypeId.Value);
            }

            if (filters.BrandId.HasValue)
            {
                query = query.Where(a => a.Listing != null && a.Listing.BrandId == filters.BrandId.Value);
            }

            if (criteria.AcceptingBookings.HasValue)
            {
                query = query.Where(a => a.AcceptingBookings == criteria.AcceptingBookings.Value);
            }

            if (filters.ManufactureYearFrom.HasValue)
            {
                query = query.Where(a => a.ManufactureYear.HasValue && a.ManufactureYear.Value >= filters.ManufactureYearFrom.Value);
            }

            if (filters.ManufactureYearTo.HasValue)
            {
                query = query.Where(a => a.ManufactureYear.HasValue && a.ManufactureYear.Value <= filters.ManufactureYearTo.Value);
            }

            if (filters.DynamicFilters != null && filters.DynamicFilters.Count > 0)
            {
                if (filters.CompatibleSchemaVersions == null || filters.CompatibleSchemaVersions.Count == 0)
                {
                    return Task.FromResult<IReadOnlyList<InventoryAsset>>(Array.Empty<InventoryAsset>());
                }

                query = query.Where(a => a.Listing != null && filters.CompatibleSchemaVersions.Contains(a.Listing.SchemaVersion));
            }

            var list = query
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)
                .Skip(criteria.Skip)
                .Take(criteria.Take)
                .ToList();

            return Task.FromResult<IReadOnlyList<InventoryAsset>>(list);
        }

        public Task<int> CountForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            var query = Store.Values.Where(a => a.Listing?.OwnerUserId == criteria.OwnerUserId);
            var filters = criteria.Filters;

            if (!string.IsNullOrWhiteSpace(filters.SearchKeyword))
            {
                query = query.Where(a => (a.Listing != null && a.Listing.Model.Contains(filters.SearchKeyword, StringComparison.OrdinalIgnoreCase)) ||
                                         (a.SerialNumber != null && a.SerialNumber.Contains(filters.SearchKeyword, StringComparison.OrdinalIgnoreCase)));
            }

            if (filters.EquipmentTypeId.HasValue)
            {
                query = query.Where(a => a.Listing != null && a.Listing.EquipmentTypeId == filters.EquipmentTypeId.Value);
            }

            if (filters.BrandId.HasValue)
            {
                query = query.Where(a => a.Listing != null && a.Listing.BrandId == filters.BrandId.Value);
            }

            if (criteria.AcceptingBookings.HasValue)
            {
                query = query.Where(a => a.AcceptingBookings == criteria.AcceptingBookings.Value);
            }

            if (filters.ManufactureYearFrom.HasValue)
            {
                query = query.Where(a => a.ManufactureYear.HasValue && a.ManufactureYear.Value >= filters.ManufactureYearFrom.Value);
            }

            if (filters.ManufactureYearTo.HasValue)
            {
                query = query.Where(a => a.ManufactureYear.HasValue && a.ManufactureYear.Value <= filters.ManufactureYearTo.Value);
            }

            if (filters.DynamicFilters != null && filters.DynamicFilters.Count > 0)
            {
                if (filters.CompatibleSchemaVersions == null || filters.CompatibleSchemaVersions.Count == 0)
                {
                    return Task.FromResult(0);
                }

                query = query.Where(a => a.Listing != null && filters.CompatibleSchemaVersions.Contains(a.Listing.SchemaVersion));
            }

            return Task.FromResult(query.Count());
        }

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            var query = Store.Values.Where(a => a.Listing?.OwnerUserId == criteria.OwnerUserId);
            var filters = criteria.Filters;

            if (filters.EquipmentTypeId.HasValue)
            {
                query = query.Where(a => a.Listing != null && a.Listing.EquipmentTypeId == filters.EquipmentTypeId.Value);
            }

            if (criteria.AcceptingBookings.HasValue)
            {
                query = query.Where(a => a.AcceptingBookings == criteria.AcceptingBookings.Value);
            }
            return Task.FromResult<IReadOnlyList<int>>(query.Where(a => a.Listing != null).Select(a => a.Listing.SchemaVersion).Distinct().OrderBy(v => v).ToList());
        }

        public Task<IReadOnlyList<InventoryAsset>> GetAssetsByListingIdAsync(Guid listingId, int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InventoryAsset>>(Store.Values.Where(a => a.EquipmentListingId == listingId).Skip(skip).Take(take).ToList());

        public Task<int> CountAssetsByListingIdAsync(Guid listingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.Values.Count(a => a.EquipmentListingId == listingId));

        public Task<InventoryAsset?> GetCalendarSnapshotAsync(Guid assetId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(assetId, out var a) ? a : null);

        public Task<IReadOnlyList<InventoryAsset>> GetAvailabilitySnapshotsAsync(IReadOnlyList<Guid> assetIds, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InventoryAsset>>(Store.Values.Where(a => assetIds.Contains(a.Id)).ToList());

        public void Add(InventoryAsset asset) => Store[asset.Id] = asset;
        public void Update(InventoryAsset asset) => Store[asset.Id] = asset;
        public Task<bool> ExistsSerialInListingAsync(Guid listingId, string serialNumber, Guid? excludeAssetId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.Values.Any(a => a.EquipmentListingId == listingId &&
                (!excludeAssetId.HasValue || a.Id != excludeAssetId.Value) &&
                string.Equals(a.SerialNumber.Trim(), serialNumber.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class FakeWriteContext : IInventoryWriteContext
    {
        public Guid? SearchBackfillRunId => null;
        public Guid AssetId { get; }
        public IInventoryAssetRepository Assets { get; }
        public Guid? ListingId => null;
        public IEquipmentListingRepository Listings => throw new NotImplementedException();
        public Task AcquireAssetLocksAsync(IEnumerable<Guid> assetIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IAssetAllocationRepository Allocations => throw new NotImplementedException();
        public ICalendarBlockRepository CalendarBlocks => throw new NotImplementedException();
        public List<OutboxMessage> StagedOutbox { get; } = new();
        public Task<long> GetNextSearchRevisionAsync(CancellationToken cancellationToken = default) => Task.FromResult(1L);

        public FakeWriteContext(Guid assetId, IInventoryAssetRepository assets)
        {
            AssetId = assetId;
            Assets = assets;
        }

        public void StageOutbox(OutboxMessage message)
        {
            StagedOutbox.Add(message);
        }
    }

    private sealed class FakeWriteTransaction : IInventoryWriteTransaction
    {
        private readonly FakeAssetRepository _repo;
        public Action? BeforeExecute { get; set; }
        public List<OutboxMessage> StagedOutbox { get; } = new();

        public FakeWriteTransaction(FakeAssetRepository repo)
        {
            _repo = repo;
        }

        public async Task<Result<T>> ExecuteAsync<T>(
            Guid assetId,
            Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> action,
            CancellationToken cancellationToken = default)
        {
            BeforeExecute?.Invoke();
            var ctx = new FakeWriteContext(assetId, _repo);
            var result = await action(ctx, cancellationToken);
            if (result.IsSuccess)
            {
                StagedOutbox.AddRange(ctx.StagedOutbox);
            }
            return result;
        }

        public Task<Result<T>> ExecuteListingWriteAsync<T>(
            Guid listingId,
            Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> action,
            CancellationToken cancellationToken = default)
        {
            BeforeExecute?.Invoke();
            var ctx = new FakeWriteContext(Guid.Empty, _repo);
            return action(ctx, cancellationToken);
        }

        public Task<Result<T>> ExecuteGroupHoldAsync<T>(
            string sourceSystem,
            string groupReference,
            Guid listingId,
            Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> action,
            CancellationToken cancellationToken = default)
        {
            BeforeExecute?.Invoke();
            var ctx = new FakeWriteContext(Guid.Empty, _repo);
            return action(ctx, cancellationToken);
        }

        public Task<Result<T>> ExecuteAllocationCommandAsync<T>(
            string sourceSystem,
            string operationKey,
            string groupReference,
            Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> action,
            CancellationToken cancellationToken = default)
        {
            BeforeExecute?.Invoke();
            var ctx = new FakeWriteContext(Guid.Empty, _repo);
            return action(ctx, cancellationToken);
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

        public Task<Result<IReadOnlyList<Guid>>> GetEquipmentTypeIdsByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<Guid>>(Array.Empty<Guid>()));
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
                : Task.FromResult(Result.Success(new MediaVerificationInfo(mediaId, "Ready", Guid.NewGuid().ToString("D"), "image/jpeg")));
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
        var queryValidator = new AssetSearchQueryValidator(_clock);
        var filterResolver = new AssetSearchFilterResolver(_catalogClient);

        return new InventoryAssetService(_repo, writeTx, _catalogClient, mediaService, queryValidator, filterResolver, _clock);
    }

    private static readonly RentalRateDto DefaultRateDto = new(100_000m, null, null, "VND");

    private static InventoryAsset CreateAssetWithRate(
        Guid? id = null,
        Guid? ownerUserId = null,
        Guid? eqTypeId = null,
        Guid? brandId = null,
        string model = "FX3",
        int schemaVersion = 1,
        string specValues = "{}",
        DateTimeOffset? now = null,
        string? serialNumber = null,
        int? manufactureYear = null,
        Guid? thumbnailMediaId = null)
    {
        var actualId = id ?? Guid.NewGuid();
        var actualNow = now ?? FixedNow;
        var listing = EquipmentListing.Create(
            Guid.NewGuid(),
            ownerUserId ?? Guid.NewGuid(),
            eqTypeId ?? Guid.NewGuid(),
            brandId ?? Guid.NewGuid(),
            model,
            null,
            model,
            schemaVersion,
            specValues,
            100_000m,
            null,
            null,
            "VND",
            actualNow,
            manufactureYear ?? 2024,
            thumbnailMediaId.HasValue ? [thumbnailMediaId.Value] : null,
            thumbnailMediaId).Value;

        var actualSerial = string.IsNullOrWhiteSpace(serialNumber) ? $"SN-{actualId:N}" : serialNumber;
        var asset = InventoryAsset.Create(
            actualId,
            listing.Id,
            actualSerial,
            actualNow,
            manufactureYear).Value;

        typeof(InventoryAsset).GetProperty(nameof(InventoryAsset.Listing))?.SetValue(asset, listing);
        return asset;
    }
}
