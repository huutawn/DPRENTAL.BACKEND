using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Warehouse;
using Dprental.InventoryService.Application.DependencyInjection;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Api.Tests.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class PostgresWarehouseIntegrationTests
{
    private const string PreviousMigration = "20261015120000_AddEquipmentListingDepositAmount";
    private const string TargetMigration = "20261016024713_AddWarehouseAndListingForeignKey";

    [Fact]
    public async Task Migration_BackfillsExistingOwners_AndCreatesForeignKey()
    {
        var fixture = new PostgresFixture { TargetMigration = PreviousMigration };
        await fixture.InitializeAsync();

        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;

            var ownerId = Guid.NewGuid();
            var listingId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            await using (var db = new InventoryServiceDbContext(options))
            {
                // Thêm listing mẫu bằng SQL phù hợp schema cũ khi chưa có bảng warehouses
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO inventory.equipment_listings
                        (id, owner_user_id, equipment_type_id, brand_id, name, model, description,
                         schema_version, spec_values, media_ids, manufacture_year, daily_rate, currency, status, deposit_amount, created_at, updated_at)
                    VALUES
                        ({0}, {1}, {2}, {3}, 'Sony A7S3', 'A7S3', 'Description',
                         1, {4}::jsonb, ARRAY[]::uuid[], 2024, 200000, 'VND', 1, 500000, {5}, {5});
                """, listingId, ownerId, Guid.NewGuid(), Guid.NewGuid(), "{}", now);
            }

            // Chạy migration đích: tạo bảng warehouses, backfill owner và tạo FK
            await using (var db = new InventoryServiceDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync(TargetMigration);
            }

            // Kiểm tra bảng warehouses và dữ liệu backfill
            await using (var db = new InventoryServiceDbContext(options))
            {
                var tableExists = await db.Database.SqlQueryRaw<bool>(
                    "SELECT to_regclass('inventory.warehouses') IS NOT NULL AS \"Value\"").SingleAsync();
                Assert.True(tableExists);

                var backfilled = await db.Warehouses.FirstOrDefaultAsync(w => w.OwnerUserId == ownerId);
                Assert.NotNull(backfilled);
                Assert.False(backfilled.HasValidAddress);

                // Kiểm tra ràng buộc khóa ngoại tồn tại
                var hasFk = await db.Database.SqlQueryRaw<bool>("""
                    SELECT EXISTS (
                        SELECT 1 FROM information_schema.table_constraints tc
                        WHERE tc.constraint_schema = 'inventory'
                          AND tc.table_name = 'equipment_listings'
                          AND tc.constraint_name = 'FK_equipment_listings_warehouses_owner_user_id'
                    ) AS "Value"
                """).SingleAsync();
                Assert.True(hasFk);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostgreSQL_Search_FiltersByProvinceAndWardCorrectly()
    {
        var fixture = new PostgresFixture();
        await fixture.InitializeAsync();

        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;

            var ownerHanoi = Guid.NewGuid();
            var ownerHcm = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            await using (var db = new InventoryServiceDbContext(options))
            {
                // Owner 1: Hà Nội, Hoàn Kiếm
                var whHanoi = Warehouse.CreateWithAddress(
                    ownerHanoi, "1 Tràng Tiền", "01", "Thành phố Hà Nội", "00070", "Phường Hoàn Kiếm", 21.0, 105.8, now).Value;
                db.Warehouses.Add(whHanoi);

                // Owner 2: TP.HCM, Thủ Dầu Một
                var whHcm = Warehouse.CreateWithAddress(
                    ownerHcm, "123 Lê Lợi", "79", "Thành phố Hồ Chí Minh", "25747", "Phường Thủ Dầu Một", 10.7, 106.7, now).Value;
                db.Warehouses.Add(whHcm);

                // Listing 1 của Hà Nội (Active + unit)
                var l1 = EquipmentListing.Create(
                    Guid.NewGuid(), ownerHanoi, Guid.NewGuid(), Guid.NewGuid(), "Cam Hanoi", null, "FX3", 1, "{}", 100_000m, null, null, "VND", now, 2024).Value;
                l1.UpdateStatus(EquipmentListingStatus.Active, true, now, true);
                db.EquipmentListings.Add(l1);

                var a1 = InventoryAsset.Create(Guid.NewGuid(), l1.Id, "SN-HN-01", now).Value;
                db.Assets.Add(a1);

                // Listing 2 của HCM (Active + unit)
                var l2 = EquipmentListing.Create(
                    Guid.NewGuid(), ownerHcm, Guid.NewGuid(), Guid.NewGuid(), "Cam HCM", null, "FX3", 1, "{}", 100_000m, null, null, "VND", now, 2024).Value;
                l2.UpdateStatus(EquipmentListingStatus.Active, true, now, true);
                db.EquipmentListings.Add(l2);

                var a2 = InventoryAsset.Create(Guid.NewGuid(), l2.Id, "SN-HCM-01", now).Value;
                db.Assets.Add(a2);

                await db.SaveChangesAsync();
            }

            await using (var db = new InventoryServiceDbContext(options))
            {
                var repo = new EquipmentListingRepository(db);

                // 1. Lọc theo tỉnh Hà Nội (01)
                var searchHanoi = await repo.SearchPublicAsync(new EquipmentListingSearchCriteria(
                    new AssetSearchFilters(ProvinceCode: "01"), skip: 0, take: 10));
                Assert.Equal(1, searchHanoi.TotalCount);
                Assert.Equal("Cam Hanoi", searchHanoi.Items[0].Listing.Name);
                Assert.NotNull(searchHanoi.Items[0].Listing.Warehouse);
                Assert.Equal("01", searchHanoi.Items[0].Listing.Warehouse!.ProvinceCode);

                // 2. Lọc theo tỉnh HCM (79)
                var searchHcm = await repo.SearchPublicAsync(new EquipmentListingSearchCriteria(
                    new AssetSearchFilters(ProvinceCode: "79"), skip: 0, take: 10));
                Assert.Equal(1, searchHcm.TotalCount);
                Assert.Equal("Cam HCM", searchHcm.Items[0].Listing.Name);

                // 3. Lọc theo tỉnh HCM và đúng phường (25747)
                var searchHcmWard = await repo.SearchPublicAsync(new EquipmentListingSearchCriteria(
                    new AssetSearchFilters(ProvinceCode: "79", WardCode: "25747"), skip: 0, take: 10));
                Assert.Equal(1, searchHcmWard.TotalCount);

                // 4. Lọc theo tỉnh HCM và phường khác (00070) -> 0 kết quả
                var searchNone = await repo.SearchPublicAsync(new EquipmentListingSearchCriteria(
                    new AssetSearchFilters(ProvinceCode: "79", WardCode: "00070"), skip: 0, take: 10));
                Assert.Equal(0, searchNone.TotalCount);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);

    private sealed class StubCatalogClient : ICatalogServiceClient
    {
        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(id, "FX3", "Sony FX3", null, 1, DateTime.UtcNow, null, DateTime.UtcNow)));
        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new CatalogBrandResponse(id, "SONY", "Sony", true)));
        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class StubMediaClient : IMediaServiceClient
    {
        public Task<Result<Dprental.InventoryService.Application.DTOs.Media.MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new Dprental.InventoryService.Application.DTOs.Media.MediaVerificationInfo(mediaId, "Ready", Guid.NewGuid().ToString("D"), "image/jpeg")));
        public Task<Result<Dprental.InventoryService.Application.DTOs.Media.MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new Dprental.InventoryService.Application.DTOs.Media.MediaVisibilityInfo(mediaId, "Public")));
    }

    private sealed class TestCaller : IInventoryCallerContext
    {
        public Guid? UserId { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsService { get; set; }
        public bool IsAuthenticated { get; set; } = true;
        public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string> { InventoryAccessEvaluator.PermissionAssetManage };
        public IReadOnlySet<string> Scopes { get; set; } = new HashSet<string>();
        public IReadOnlySet<string> Roles { get; set; } = new HashSet<string>();
        public Guid? OrganizationId { get; set; }
    }

    private static ServiceProvider BuildServiceProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInventoryServiceDomainRepositories(connectionString);
        services.AddInventoryApplication();
        services.AddSingleton<TimeProvider>(new TestTimeProvider(FixedNow));
        services.Configure<MediaServiceOptions>(o => o.PublicGatewayUrl = "https://gateway.example");
        services.AddSingleton<ICatalogServiceClient, StubCatalogClient>();
        services.AddSingleton<IMediaServiceClient, StubMediaClient>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Concurrency_TwoFirstListingCreations_SameOwner_BothSucceedWithSingleWarehouse()
    {
        var fixture = new PostgresFixture();
        await fixture.InitializeAsync();

        try
        {
            await using var sp = BuildServiceProvider(fixture.ConnectionString);
            var ownerId = Guid.NewGuid();
            var caller = new TestCaller { UserId = ownerId };

            var barrier = new Barrier(2);
            using var doc = JsonDocument.Parse("{}");
            var req1 = new CreateEquipmentListingRequest(
                Guid.NewGuid(), Guid.NewGuid(), "Listing 1", "Model 1", "SN-CONC-001",
                doc.RootElement, new RentalRateDto(100_000m, null, null, "VND"), 2024);
            var req2 = new CreateEquipmentListingRequest(
                Guid.NewGuid(), Guid.NewGuid(), "Listing 2", "Model 2", "SN-CONC-002",
                doc.RootElement, new RentalRateDto(200_000m, null, null, "VND"), 2024);

            var task1 = Task.Run(async () =>
            {
                using var scope = sp.CreateScope();
                var listingService = scope.ServiceProvider.GetRequiredService<IEquipmentListingService>();
                barrier.SignalAndWait();
                return await listingService.CreateListingAsync(req1, caller);
            });

            var task2 = Task.Run(async () =>
            {
                using var scope = sp.CreateScope();
                var listingService = scope.ServiceProvider.GetRequiredService<IEquipmentListingService>();
                barrier.SignalAndWait();
                return await listingService.CreateListingAsync(req2, caller);
            });

            var results = await Task.WhenAll(task1, task2);
            Assert.True(results[0].IsSuccess, results[0].IsFailure ? results[0].Error.ToString() : null);
            Assert.True(results[1].IsSuccess, results[1].IsFailure ? results[1].Error.ToString() : null);

            // Verify exactly one warehouse was created for this owner
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new InventoryServiceDbContext(options);
            var warehouseCount = await db.Warehouses.CountAsync(w => w.OwnerUserId == ownerId);
            Assert.Equal(1, warehouseCount);

            var listingCount = await db.EquipmentListings.CountAsync(l => l.OwnerUserId == ownerId);
            Assert.Equal(2, listingCount);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Concurrency_ListingCreationAndPutWarehouse_DoNotConflict_AndPreservesConfiguredAddress()
    {
        var fixture = new PostgresFixture();
        await fixture.InitializeAsync();

        try
        {
            await using var sp = BuildServiceProvider(fixture.ConnectionString);
            var ownerId = Guid.NewGuid();
            var caller = new TestCaller { UserId = ownerId };

            var barrier = new Barrier(2);
            using var doc = JsonDocument.Parse("{}");
            var listingReq = new CreateEquipmentListingRequest(
                Guid.NewGuid(), Guid.NewGuid(), "Listing Concurrent", "Model 1", "SN-CONC-003",
                doc.RootElement, new RentalRateDto(100_000m, null, null, "VND"), 2024);

            var whReq = new UpdateWarehouseRequest("123 Phố Huế", "01", "00070", 21.01, 105.85);

            var taskListing = Task.Run(async () =>
            {
                using var scope = sp.CreateScope();
                var listingService = scope.ServiceProvider.GetRequiredService<IEquipmentListingService>();
                barrier.SignalAndWait();
                return await listingService.CreateListingAsync(listingReq, caller);
            });

            var taskWarehouse = Task.Run(async () =>
            {
                using var scope = sp.CreateScope();
                var warehouseService = scope.ServiceProvider.GetRequiredService<IWarehouseService>();
                barrier.SignalAndWait();
                return await warehouseService.UpdateMyWarehouseAsync(whReq, caller);
            });

            var (listingRes, whRes) = (await taskListing, await taskWarehouse);
            Assert.True(listingRes.IsSuccess, listingRes.IsFailure ? listingRes.Error.ToString() : null);
            Assert.True(whRes.IsSuccess, whRes.IsFailure ? whRes.Error.ToString() : null);

            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new InventoryServiceDbContext(options);
            var warehouse = await db.Warehouses.SingleAsync(w => w.OwnerUserId == ownerId);
            Assert.True(warehouse.HasValidAddress);
            Assert.Equal("123 Phố Huế", warehouse.AddressDetail);
            Assert.Equal("01", warehouse.ProvinceCode);
            Assert.Equal("00070", warehouse.WardCode);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Concurrency_TwoConcurrentPutWarehouse_SameExpectedVersion_OneWinsAndOneReturns409()
    {
        var fixture = new PostgresFixture();
        await fixture.InitializeAsync();

        try
        {
            await using var sp = BuildServiceProvider(fixture.ConnectionString);
            var ownerId = Guid.NewGuid();
            var caller = new TestCaller { UserId = ownerId };

            // 1. Tạo kho ban đầu để có ExpectedVersion
            WarehouseResponse initialWh;
            using (var scope = sp.CreateScope())
            {
                var warehouseService = scope.ServiceProvider.GetRequiredService<IWarehouseService>();
                var initialRes = await warehouseService.UpdateMyWarehouseAsync(
                    new UpdateWarehouseRequest("123 Le Loi", "01", "00070", 21.0, 105.8), caller);
                Assert.True(initialRes.IsSuccess);
                initialWh = initialRes.Value;
            }

            var barrier = new Barrier(2);
            var req1 = new UpdateWarehouseRequest("200 Le Loi", "01", "00070", 21.0, 105.8, ExpectedVersion: initialWh.Version);
            var req2 = new UpdateWarehouseRequest("300 Le Loi", "01", "00070", 21.0, 105.8, ExpectedVersion: initialWh.Version);

            var task1 = Task.Run(async () =>
            {
                using var scope = sp.CreateScope();
                var warehouseService = scope.ServiceProvider.GetRequiredService<IWarehouseService>();
                barrier.SignalAndWait();
                return await warehouseService.UpdateMyWarehouseAsync(req1, caller);
            });

            var task2 = Task.Run(async () =>
            {
                using var scope = sp.CreateScope();
                var warehouseService = scope.ServiceProvider.GetRequiredService<IWarehouseService>();
                barrier.SignalAndWait();
                return await warehouseService.UpdateMyWarehouseAsync(req2, caller);
            });

            var results = await Task.WhenAll(task1, task2);
            var successCount = results.Count(r => r.IsSuccess);
            var conflictCount = results.Count(r => r.IsFailure && r.Error.Code == "STALE_WAREHOUSE_WRITE");

            Assert.Equal(1, successCount);
            Assert.Equal(1, conflictCount);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
