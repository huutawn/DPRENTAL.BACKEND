using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
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
                    brandId, "BR-SONY", "Sony", "Active")));
        }

        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default)
        {
            return SpecHandler is not null
                ? SpecHandler(equipmentTypeId, schemaVersion, specValues)
                : Task.FromResult(Result.Success());
        }
    }

    private sealed class StubMediaClient : IMediaServiceClient
    {
        public Func<Guid, Task<Result<MediaVerificationInfo>>>? VerifyHandler { get; set; }

        public Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            return VerifyHandler is not null
                ? VerifyHandler(mediaId)
                : Task.FromResult(Result.Success(new MediaVerificationInfo(mediaId, "Ready", Guid.NewGuid().ToString("D"))));
        }

        public Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(mediaId, "Public")));
    }

    private sealed class RaceInterceptingAssetRepository(IInventoryAssetRepository inner, Action onGetById) : IInventoryAssetRepository
    {
        public async Task<InventoryAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var res = await inner.GetByIdAsync(id, cancellationToken);
            onGetById();
            return res;
        }

        public Task<InventoryAsset?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            inner.GetByIdForOwnerAsync(id, ownerUserId, cancellationToken);

        public Task<IReadOnlyList<InventoryAsset>> ListByOwnerAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default) =>
            inner.ListByOwnerAsync(ownerUserId, skip, take, cancellationToken);

        public Task<int> CountByOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) =>
            inner.CountByOwnerAsync(ownerUserId, cancellationToken);

        public Task<IReadOnlyList<InventoryAsset>> ListAllAsync(int skip, int take, CancellationToken cancellationToken = default) =>
            inner.ListAllAsync(skip, take, cancellationToken);

        public Task<InventoryAsset?> GetCalendarSnapshotAsync(Guid assetId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            inner.GetCalendarSnapshotAsync(assetId, startDate, endDate, cancellationToken);

        public Task<IReadOnlyList<InventoryAsset>> GetAvailabilitySnapshotsAsync(IReadOnlyList<Guid> assetIds, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            inner.GetAvailabilitySnapshotsAsync(assetIds, startDate, endDate, cancellationToken);

        public void Add(InventoryAsset asset) => inner.Add(asset);
        public void Update(InventoryAsset asset) => inner.Update(asset);
    }

    private (WebApplicationFactory<Program> App, InventorySecurityTestFactory SecFactory) CreateFactory(
        ICatalogServiceClient? catalogClient = null,
        IMediaServiceClient? mediaClient = null,
        TestTimeProvider? clock = null)
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

    private async Task<InventoryAsset> SeedAssetAsync(Guid ownerUserId, string model = "Sony FX3", string? serial = null, int? year = null, Guid? thumb = null, Guid? brandId = null)
    {
        await using var db = CreateDbContext();
        var asset = InventoryAsset.Create(
            Guid.NewGuid(),
            ownerUserId,
            Guid.NewGuid(),
            brandId ?? Guid.NewGuid(),
            model,
            1,
            "{\"sensor\":\"Full-Frame\"}",
            FixedNow,
            serial,
            year,
            thumb).Value;
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset;
    }
}
