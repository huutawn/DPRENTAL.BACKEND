using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

/// <summary>
/// Cấu hình test fixture, stubs và database context cho HTTP integration tests của Inventory Asset Management.
/// </summary>
public sealed partial class AssetHttpIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    public AssetHttpIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class StubCatalogClient : ICatalogServiceClient
    {
        public Func<Guid, Task<Result<CatalogEquipmentTypeResponse>>>? LookupHandler { get; set; }
        public Func<Guid, Task<Result<CatalogBrandResponse>>>? BrandHandler { get; set; }
        public Func<Guid, int, string, Task<Result>>? SpecHandler { get; set; }
        public Func<Guid, IReadOnlyList<int>, Task<Result<CatalogBatchSchemasResponse>>>? BatchSchemasHandler { get; set; }
        public int BatchCalls { get; set; }

        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default)
        {
            return LookupHandler is not null
                ? LookupHandler(equipmentTypeId)
                : Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(
                    equipmentTypeId, "EQ-FX3", "Sony FX3 Cinema Line", null, 1, DateTime.UtcNow, null, null)));
        }

        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid brandId, CancellationToken cancellationToken = default)
        {
            return BrandHandler is not null
                ? BrandHandler(brandId)
                : Task.FromResult(Result.Success(new CatalogBrandResponse(
                    brandId, "BR-SONY", "Sony", true)));
        }

        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default)
        {
            return SpecHandler is not null
                ? SpecHandler(equipmentTypeId, schemaVersion, specValues)
                : Task.FromResult(Result.Success());
        }

        public Task<Result<CatalogBatchSchemasResponse>> GetEquipmentTypeSchemasBatchAsync(Guid equipmentTypeId, IReadOnlyList<int> versions, CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            return BatchSchemasHandler is not null
                ? BatchSchemasHandler(equipmentTypeId, versions)
                : Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
                    equipmentTypeId,
                    1,
                    [new CatalogBatchSchemaItemResponse(1, [])])));
        }
    }

    private sealed class StubMediaClient : IMediaServiceClient
    {
        public Func<Guid, Task<Result<MediaVerificationInfo>>>? VerifyHandler { get; set; }
        public int VerifyCalls { get; set; }

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

    private sealed class RaceInterceptingAssetRepository(
        IInventoryAssetRepository inner,
        InventoryServiceDbContext? db = null,
        Func<InventoryAsset?, Task>? onAfterSnapshotRead = null,
        Action? onInsideLockRead = null,
        Action? onGetById = null) : IInventoryAssetRepository
    {
        public RaceInterceptingAssetRepository(IInventoryAssetRepository inner, Action onGetById)
            : this(inner, null, null, null, onGetById)
        {
        }

        public async Task<InventoryAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var res = await inner.GetByIdAsync(id, cancellationToken);
            onGetById?.Invoke();

            if (db != null && db.Database.CurrentTransaction != null)
            {
                onInsideLockRead?.Invoke();
            }
            else if (onAfterSnapshotRead != null)
            {
                await onAfterSnapshotRead(res);
            }

            return res;
        }

        public Task<InventoryAsset?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            inner.GetByIdForOwnerAsync(id, ownerUserId, cancellationToken);

        public Task<IReadOnlyList<InventoryAsset>> SearchForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            inner.SearchForOwnerAsync(criteria, cancellationToken);

        public Task<int> CountForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            inner.CountForOwnerAsync(criteria, cancellationToken);

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            inner.GetDistinctSchemaVersionsForOwnerAsync(criteria, cancellationToken);

        public Task<InventoryAsset?> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetPublicByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsPublicAsync(AssetSearchFilters filters, CancellationToken cancellationToken = default) =>
            inner.GetDistinctSchemaVersionsPublicAsync(filters, cancellationToken);

        public Task<PublicAssetSearchResult> SearchPublicAsync(PublicAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            inner.SearchPublicAsync(criteria, cancellationToken);
        public Task<InventoryAsset?> GetCalendarSnapshotAsync(Guid assetId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            inner.GetCalendarSnapshotAsync(assetId, startDate, endDate, cancellationToken);

        public Task<IReadOnlyList<InventoryAsset>> GetAvailabilitySnapshotsAsync(IReadOnlyList<Guid> assetIds, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            inner.GetAvailabilitySnapshotsAsync(assetIds, startDate, endDate, cancellationToken);

        public void Add(InventoryAsset asset) => inner.Add(asset);
        public void Update(InventoryAsset asset) => inner.Update(asset);
    }

    private sealed class ConcurrencySnapshotBarrier(int count = 2, TimeSpan? timeout = null)
    {
        private readonly TaskCompletionSource<bool>[] _arrivals = Enumerable.Range(0, count)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        private int _index;
        private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(5);

        public async Task ArriveAndAwaitAsync()
        {
            int current = Interlocked.Increment(ref _index) - 1;
            if (current < _arrivals.Length)
            {
                _arrivals[current].TrySetResult(true);
            }

            var allArrived = Task.WhenAll(_arrivals.Select(a => a.Task));
            await allArrived.WaitAsync(_timeout);
        }
    }

    private (WebApplicationFactory<Program> App, InventorySecurityTestFactory SecFactory) CreateFactory(
        ICatalogServiceClient? catalogClient = null,
        IMediaServiceClient? mediaClient = null,
        TestTimeProvider? clock = null,
        Func<IServiceProvider, IInventoryAssetRepository>? assetRepoFactory = null)
    {
        var secFactory = new InventorySecurityTestFactory(_fixture.ConnectionString, clock ?? new TestTimeProvider(FixedNow));
        var app = secFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICatalogServiceClient>();
                services.AddSingleton(catalogClient ?? new StubCatalogClient());

                services.RemoveAll<IMediaServiceClient>();
                services.AddSingleton(mediaClient ?? new StubMediaClient());

                if (assetRepoFactory != null)
                {
                    services.RemoveAll<IInventoryAssetRepository>();
                    services.AddScoped(assetRepoFactory);
                }
            });
        });
        return (app, secFactory);
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    private async Task<InventoryAsset> SeedAssetAsync(
        Guid ownerUserId,
        string model = "Sony FX3",
        string? serial = null,
        int? year = null,
        Guid? thumb = null,
        Guid? brandId = null,
        Guid? eqTypeId = null,
        int schemaVersion = 1,
        string specValues = "{\"sensor\":\"Full-Frame\"}",
        DateTimeOffset? createdAt = null)
    {
        var actualNow = createdAt ?? FixedNow;
        await using var db = CreateDbContext();
        var id = Guid.NewGuid();
        var rate = AssetRentalRate.Create(id, 100_000m, null, null, "VND", actualNow).Value;
        var asset = InventoryAsset.Create(
            id,
            ownerUserId,
            eqTypeId ?? Guid.NewGuid(),
            brandId ?? Guid.NewGuid(),
            model,
            schemaVersion,
            specValues,
            actualNow,
            rate,
            serial,
            year,
            thumb).Value;
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset;
    }
}
