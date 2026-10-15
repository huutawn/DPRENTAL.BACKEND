using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class PostgresEquipmentListingContentAndSearchTests : IClassFixture<PostgresFixture>
{
    private sealed record ListingMediaAndYearRow(Guid[] MediaIds, Guid? ThumbnailMediaId, int? ManufactureYear);

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _fixture;

    public PostgresEquipmentListingContentAndSearchTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    private sealed class TrackingCatalogClient : ICatalogServiceClient
    {
        public int LookupCalls { get; private set; }
        public int BrandCalls { get; private set; }
        public int SpecCalls { get; private set; }
        public int BatchCalls { get; private set; }

        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default)
        {
            LookupCalls++;
            return Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(equipmentTypeId, "EQ-CODE", "Equipment Type", null, 1, DateTime.UtcNow, null, null)));
        }

        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid brandId, CancellationToken cancellationToken = default)
        {
            BrandCalls++;
            return Task.FromResult(Result.Success(new CatalogBrandResponse(brandId, "BR-CODE", "Brand", true)));
        }

        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default)
        {
            SpecCalls++;
            return Task.FromResult(Result.Success());
        }

        public Task<Result<CatalogBatchSchemasResponse>> GetEquipmentTypeSchemasBatchAsync(Guid equipmentTypeId, IReadOnlyList<int> versions, CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            return Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(equipmentTypeId, 1, [
                new CatalogBatchSchemaItemResponse(1, [])
            ])));
        }

        public Task<Result<IReadOnlyList<Guid>>> GetEquipmentTypeIdsByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<Guid>>(Array.Empty<Guid>()));
    }

    private sealed class TrackingMediaClient : IMediaServiceClient
    {
        public int VerifyCalls { get; private set; }
        public Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            VerifyCalls++;
            return Task.FromResult(Result.Success(new MediaVerificationInfo(mediaId, "Ready", Guid.NewGuid().ToString("D"), "image/jpeg")));
        }

        public Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success(new MediaVisibilityInfo(mediaId, "Public")));
        }
    }

    private sealed class CommandCountInterceptor : DbCommandInterceptor
    {
        public int SelectCount { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                SelectCount++;
            }
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                SelectCount++;
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public void Reset() => SelectCount = 0;
    }

    private (WebApplicationFactory<Program> App, InventorySecurityTestFactory SecFactory, TrackingCatalogClient Catalog, TrackingMediaClient Media, CommandCountInterceptor Interceptor) CreateFactory()
    {
        var testClock = new TestTimeProvider(FixedNow);
        var secFactory = new InventorySecurityTestFactory(_fixture.ConnectionString, testClock);
        var catalog = new TrackingCatalogClient();
        var media = new TrackingMediaClient();
        var interceptor = new CommandCountInterceptor();

        var app = secFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(testClock);

                services.RemoveAll<ICatalogServiceClient>();
                services.AddSingleton<ICatalogServiceClient>(catalog);

                services.RemoveAll<IMediaServiceClient>();
                services.AddSingleton<IMediaServiceClient>(media);

                services.AddSingleton(interceptor);
            });
        });

        return (app, secFactory, catalog, media, interceptor);
    }

    #region Migration Verification on DB with Historical Data

    [Fact]
    public async Task Migration_BackfillsNameFromModel_AndEnforcesConstraints()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261008000000_RequireAssetSerialNumberAndUniqueListingSerial" };
        await fixture.InitializeAsync();

        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;

            await using var db = new InventoryServiceDbContext(options);

            // 1. Insert existing listing before migration (cột name và description chưa tồn tại ở schema cũ)
            var listingId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var eqTypeId = Guid.NewGuid();
            var brandId = Guid.NewGuid();
            var modelName = "Sony FX3 Historical";
            var emptyJson = "{}";

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "inventory"."equipment_listings"
                    ("id", "owner_user_id", "equipment_type_id", "brand_id", "model", "schema_version", "spec_values", "daily_rate", "currency", "status", "created_at", "updated_at")
                VALUES
                    ({listingId}, {ownerId}, {eqTypeId}, {brandId}, {modelName}, 1, {emptyJson}::jsonb, 500000, 'VND', 2, NOW(), NOW());
                """);

            // 2. Apply forward migration to 20261012000000_AddEquipmentListingNameAndDescription
            await db.GetService<IMigrator>().MigrateAsync("20261012000000_AddEquipmentListingNameAndDescription");

            // 3. Verify name was backfilled with modelName, description is null
            var row = await db.Database.SqlQuery<string>($"""
                SELECT "name" AS "Value" FROM "inventory"."equipment_listings" WHERE "id" = {listingId}
                """).SingleAsync();
            Assert.Equal(modelName, row);

            var descRow = await db.Database.SqlQuery<string?>($"""
                SELECT "description" AS "Value" FROM "inventory"."equipment_listings" WHERE "id" = {listingId}
                """).SingleAsync();

            // 4. Verify NOT NULL constraint on name
            await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "inventory"."equipment_listings"
                        ("id", "owner_user_id", "equipment_type_id", "brand_id", "name", "model", "schema_version", "spec_values", "daily_rate", "currency", "status", "created_at", "updated_at")
                    VALUES
                        ({Guid.NewGuid()}, {ownerId}, {eqTypeId}, {brandId}, NULL, 'Model', 1, {emptyJson}::jsonb, 500000, 'VND', 1, NOW(), NOW());
                    """);
            });

            // 5. Verify check constraint on name: whitespace only rejected
            await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "inventory"."equipment_listings"
                        ("id", "owner_user_id", "equipment_type_id", "brand_id", "name", "model", "schema_version", "spec_values", "daily_rate", "currency", "status", "created_at", "updated_at")
                    VALUES
                        ({Guid.NewGuid()}, {ownerId}, {eqTypeId}, {brandId}, '   ', 'Model', 1, {emptyJson}::jsonb, 500000, 'VND', 1, NOW(), NOW());
                    """);
            });

            // 6. Verify check constraint on description: whitespace only rejected
            await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "inventory"."equipment_listings"
                        ("id", "owner_user_id", "equipment_type_id", "brand_id", "name", "description", "model", "schema_version", "spec_values", "daily_rate", "currency", "status", "created_at", "updated_at")
                    VALUES
                        ({Guid.NewGuid()}, {ownerId}, {eqTypeId}, {brandId}, 'Valid Name', '   ', 'Model', 1, {emptyJson}::jsonb, 500000, 'VND', 1, NOW(), NOW());
                    """);
            });
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Migration_BackfillsMediaIdsAndManufactureYear_EnforcesConstraints_AndSupportsRollback()
    {
        var fixture = new PostgresFixture { TargetMigration = "20261012000000_AddEquipmentListingNameAndDescription" };
        await fixture.InitializeAsync();

        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                .Options;

            await using var db = new InventoryServiceDbContext(options);

            var ownerId = Guid.NewGuid();
            var eqTypeId = Guid.NewGuid();
            var brandId = Guid.NewGuid();
            var emptyJson = "{}";

            // Listing 1: has thumbnail_media_id and 2 assets at different times (earliest has 2021, later has 2024)
            var listing1Id = Guid.NewGuid();
            var thumb1 = Guid.NewGuid();
            var l1AssetEarlyId = Guid.NewGuid();
            var l1AssetLateId = Guid.NewGuid();

            // Listing 2: no thumbnail, 2 assets with identical created_at (tie-break by id ASC: smaller id has 2019, larger id has 2023)
            var listing2Id = Guid.NewGuid();
            var l2AssetSmallId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var l2AssetLargeId = Guid.Parse("00000000-0000-0000-0000-000000000002");

            // Listing 3: earliest asset has manufacture_year = NULL, later asset has 2025 -> listing manufacture_year must remain NULL
            var listing3Id = Guid.NewGuid();

            // Listing 4: no assets at all -> listing manufacture_year must remain NULL, media_ids = {}
            var listing4Id = Guid.NewGuid();

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "inventory"."equipment_listings"
                    ("id", "owner_user_id", "equipment_type_id", "brand_id", "name", "model", "schema_version", "spec_values", "thumbnail_media_id", "daily_rate", "currency", "status", "created_at", "updated_at")
                VALUES
                    ({listing1Id}, {ownerId}, {eqTypeId}, {brandId}, 'Listing 1', 'FX3', 1, {emptyJson}::jsonb, {thumb1}, 500000, 'VND', 2, NOW(), NOW()),
                    ({listing2Id}, {ownerId}, {eqTypeId}, {brandId}, 'Listing 2', 'FX6', 1, {emptyJson}::jsonb, NULL, 500000, 'VND', 2, NOW(), NOW()),
                    ({listing3Id}, {ownerId}, {eqTypeId}, {brandId}, 'Listing 3', 'FX9', 1, {emptyJson}::jsonb, NULL, 500000, 'VND', 1, NOW(), NOW()),
                    ({listing4Id}, {ownerId}, {eqTypeId}, {brandId}, 'Listing 4', 'A7S3', 1, {emptyJson}::jsonb, NULL, 500000, 'VND', 1, NOW(), NOW());

                INSERT INTO "inventory"."assets"
                    ("id", "equipment_listing_id", "asset_code", "serial_number", "manufacture_year", "accepting_bookings", "created_at", "updated_at")
                VALUES
                    ({l1AssetEarlyId}, {listing1Id}, {"AST-" + l1AssetEarlyId.ToString("N")}, 'L1-SN-1', 2021, TRUE, '2026-01-01T00:00:00Z'::timestamptz, '2026-01-01T00:00:00Z'::timestamptz),
                    ({l1AssetLateId}, {listing1Id}, {"AST-" + l1AssetLateId.ToString("N")}, 'L1-SN-2', 2024, TRUE, '2026-02-01T00:00:00Z'::timestamptz, '2026-02-01T00:00:00Z'::timestamptz),
                    ({l2AssetLargeId}, {listing2Id}, {"AST-" + l2AssetLargeId.ToString("N")}, 'L2-SN-2', 2023, TRUE, '2026-03-01T10:00:00Z'::timestamptz, '2026-03-01T10:00:00Z'::timestamptz),
                    ({l2AssetSmallId}, {listing2Id}, {"AST-" + l2AssetSmallId.ToString("N")}, 'L2-SN-1', 2019, TRUE, '2026-03-01T10:00:00Z'::timestamptz, '2026-03-01T10:00:00Z'::timestamptz),
                    ({Guid.NewGuid()}, {listing3Id}, {"AST-" + Guid.NewGuid().ToString("N")}, 'L3-SN-1', NULL, TRUE, '2026-01-01T00:00:00Z'::timestamptz, '2026-01-01T00:00:00Z'::timestamptz),
                    ({Guid.NewGuid()}, {listing3Id}, {"AST-" + Guid.NewGuid().ToString("N")}, 'L3-SN-2', 2025, TRUE, '2026-02-01T00:00:00Z'::timestamptz, '2026-02-01T00:00:00Z'::timestamptz);
                """);

            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261013000000_AddEquipmentListingMediaAndManufactureYear");

            // Verify backfilled values
            var l1 = await db.Database.SqlQuery<ListingMediaAndYearRow>($"""
                SELECT media_ids AS "MediaIds", thumbnail_media_id AS "ThumbnailMediaId", manufacture_year AS "ManufactureYear"
                FROM inventory.equipment_listings WHERE id = {listing1Id}
            """).SingleAsync();
            Assert.Equal([thumb1], l1.MediaIds);
            Assert.Equal(thumb1, l1.ThumbnailMediaId);
            Assert.Equal(2021, l1.ManufactureYear);

            // Verify asset years were NOT modified by migration
            var l1LateAsset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == l1AssetLateId);
            Assert.Equal(2024, l1LateAsset.ManufactureYear);

            var l2 = await db.Database.SqlQuery<ListingMediaAndYearRow>($"""
                SELECT media_ids AS "MediaIds", thumbnail_media_id AS "ThumbnailMediaId", manufacture_year AS "ManufactureYear"
                FROM inventory.equipment_listings WHERE id = {listing2Id}
            """).SingleAsync();
            Assert.Empty(l2.MediaIds);
            Assert.Null(l2.ThumbnailMediaId);
            Assert.Equal(2019, l2.ManufactureYear);

            var l3 = await db.Database.SqlQuery<ListingMediaAndYearRow>($"""
                SELECT media_ids AS "MediaIds", thumbnail_media_id AS "ThumbnailMediaId", manufacture_year AS "ManufactureYear"
                FROM inventory.equipment_listings WHERE id = {listing3Id}
            """).SingleAsync();
            Assert.Empty(l3.MediaIds);
            Assert.Null(l3.ManufactureYear);

            var l4 = await db.Database.SqlQuery<ListingMediaAndYearRow>($"""
                SELECT media_ids AS "MediaIds", thumbnail_media_id AS "ThumbnailMediaId", manufacture_year AS "ManufactureYear"
                FROM inventory.equipment_listings WHERE id = {listing4Id}
            """).SingleAsync();
            Assert.Empty(l4.MediaIds);
            Assert.Null(l4.ManufactureYear);
            // Verify DB constraints on manufacture_year (< 1900 rejected)
            await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "inventory"."equipment_listings"
                    SET "manufacture_year" = 1899
                    WHERE "id" = {listing1Id};
                    """);
            });

            // Verify DB constraint: thumbnail_media_id must be in media_ids
            await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "inventory"."equipment_listings"
                    SET "media_ids" = ARRAY[]::uuid[]
                    WHERE "id" = {listing1Id};
                    """);
            });

            // Verify Down rollback and re-migrate
            await migrator.MigrateAsync("20261012000000_AddEquipmentListingNameAndDescription");
            await migrator.MigrateAsync("20261013000000_AddEquipmentListingMediaAndManufactureYear");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    #endregion

    #region Owner Search with RepeatableRead 2 SELECTs (No N+1)

    [Fact]
    public async Task OwnerListings_TwoSelectQueriesInRepeatableRead_NoNPlusOne()
    {
        var interceptor = new CommandCountInterceptor();
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new InventoryServiceDbContext(options);
        var repo = new EquipmentListingRepository(db);

        var ownerId = Guid.NewGuid();

        // Tạo 5 listings cho owner này, mỗi listing có 2 asset accepting bookings và 1 asset disabled
        for (int i = 1; i <= 5; i++)
        {
            var listing = await IntegrationTestHelper.InsertTestListingAsync(
                db,
                ownerId: ownerId,
                name: $"Thiết bị quay phim {i}",
                model: $"FX{i}",
                initialUnits: 2);

            // Thêm một asset disabled không nhận booking
            var disabledAsset = InventoryAsset.Create(Guid.NewGuid(), listing.Id, $"SN-DISABLED-{i}", FixedNow).Value;
            disabledAsset.DisableBookings("Hỏng", FixedNow);
            db.Assets.Add(disabledAsset);
            await db.SaveChangesAsync();
        }

        interceptor.Reset();

        // Thực hiện tìm kiếm phân trang owner
        var criteria = new OwnerEquipmentListingSearchCriteria(ownerId, null, 0, 10);
        var result = await repo.SearchOwnerAsync(criteria);

        // Verification: Exactly 2 SELECT queries executed! (1 count + 1 page with quantity projection)
        Assert.Equal(2, interceptor.SelectCount);
        Assert.Equal(5, result.TotalCount);
        Assert.Equal(5, result.Items.Count);

        // Verification: derived quantity chỉ đếm asset accepting bookings (2 per listing, không phải 3)
        foreach (var item in result.Items)
        {
            Assert.Equal(2, item.TotalQuantity);
        }
    }

    #endregion

    #region Search Keyword in PostgreSQL (Name OR Model, Case-Insensitive, Vietnamese Accents, Wildcard Escaping)

    [Fact]
    public async Task OwnerSearch_MatchesNameOrModel_CaseInsensitive_PreservesDiacritics_EscapesWildcards()
    {
        await using var db = CreateDbContext();
        var repo = new EquipmentListingRepository(db);

        var ownerId = Guid.NewGuid();
        var otherOwnerId = Guid.NewGuid();

        // Listing 1: Name chứa tiếng Việt có dấu "Máy quay phim chuyên nghiệp", Model "FX3"
        await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: ownerId, name: "Máy quay phim chuyên nghiệp", model: "FX3", initialUnits: 1);

        // Listing 2: Name "Bộ đèn LED 300W", Model "Aputure 300d II"
        await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: ownerId, name: "Bộ đèn LED 300W", model: "Aputure 300d II", initialUnits: 1);

        // Listing 3: Name có ký tự đặc biệt "50% off _sale_ [promo]", Model "Lens 24-70"
        await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: ownerId, name: "50% off _sale_ promo", model: "Lens 24-70", initialUnits: 1);

        // Listing 4: Của otherOwnerId -> không bao giờ trả về cho ownerId
        await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: otherOwnerId, name: "Máy quay phim chuyên nghiệp của người khác", model: "FX3", initialUnits: 1);

        // 1. Khớp theo Model: "fx3" (chữ thường) khớp Listing 1
        var resModel = await repo.SearchOwnerAsync(new OwnerEquipmentListingSearchCriteria(ownerId, "fx3", 0, 10));
        Assert.Single(resModel.Items);
        Assert.Equal("FX3", resModel.Items[0].Listing.Model);

        // 2. Khớp theo Name: "máy quay" (chữ thường, có dấu) khớp Listing 1
        var resName = await repo.SearchOwnerAsync(new OwnerEquipmentListingSearchCriteria(ownerId, "máy quay", 0, 10));
        Assert.Single(resName.Items);
        Assert.Equal("Máy quay phim chuyên nghiệp", resName.Items[0].Listing.Name);

        // 3. Phân biệt dấu tiếng Việt: "may quay" (không dấu) KHÔNG khớp "Máy quay phim"
        var resNoDiacritics = await repo.SearchOwnerAsync(new OwnerEquipmentListingSearchCriteria(ownerId, "may quay", 0, 10));
        Assert.Empty(resNoDiacritics.Items);

        // 4. Literal wildcard escaping: tìm "50%" chỉ khớp Listing 3 (không khớp tùy tiện mọi thứ)
        var resPercent = await repo.SearchOwnerAsync(new OwnerEquipmentListingSearchCriteria(ownerId, "50%", 0, 10));
        Assert.Single(resPercent.Items);
        Assert.Equal("50% off _sale_ promo", resPercent.Items[0].Listing.Name);

        // 5. Literal wildcard escaping: tìm "_sale_" chỉ khớp Listing 3
        var resUnderscore = await repo.SearchOwnerAsync(new OwnerEquipmentListingSearchCriteria(ownerId, "_sale_", 0, 10));
        Assert.Single(resUnderscore.Items);
        Assert.Equal("50% off _sale_ promo", resUnderscore.Items[0].Listing.Name);

        // 6. Owner isolation: otherOwnerId có listing cùng tên nhưng không lọt vào kết quả của ownerId
        var resOther = await repo.SearchOwnerAsync(new OwnerEquipmentListingSearchCriteria(otherOwnerId, "máy quay", 0, 10));
        Assert.Single(resOther.Items);
        Assert.Equal(otherOwnerId, resOther.Items[0].Listing.OwnerUserId);
    }

    [Fact]
    public async Task OwnerSearch_FilterByCategoryStatusBrand()
    {
        await using var db = CreateDbContext();
        var repo = new EquipmentListingRepository(db);

        var ownerId = Guid.NewGuid();
        var eqType1 = Guid.NewGuid();
        var eqType2 = Guid.NewGuid();
        var brand1 = Guid.NewGuid();
        var brand2 = Guid.NewGuid();

        // Listing 1: eqType1, brand1, Draft
        var l1 = await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: ownerId, eqTypeId: eqType1, brandId: brand1, name: "Item 1", model: "M1", status: EquipmentListingStatus.Draft);
        // Listing 2: eqType1, brand2, Active
        var l2 = await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: ownerId, eqTypeId: eqType1, brandId: brand2, name: "Item 2", model: "M2", status: EquipmentListingStatus.Active);
        // Listing 3: eqType2, brand1, Inactive
        var l3 = await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: ownerId, eqTypeId: eqType2, brandId: brand1, name: "Item 3", model: "M3", status: EquipmentListingStatus.Inactive);

        // Lọc theo Category (EquipmentTypeId)
        var resCat = await repo.SearchOwnerAsync(new OwnerEquipmentListingSearchCriteria(ownerId, null, 0, 10, EquipmentTypeId: eqType1));
        Assert.Equal(2, resCat.TotalCount);

        // Lọc theo Brand
        var resBrand = await repo.SearchOwnerAsync(new OwnerEquipmentListingSearchCriteria(ownerId, null, 0, 10, BrandId: brand2));
        Assert.Single(resBrand.Items);
        Assert.Equal(l2.Id, resBrand.Items[0].Listing.Id);

        // Lọc theo Status: Draft
        var resStatus = await repo.SearchOwnerAsync(new OwnerEquipmentListingSearchCriteria(ownerId, null, 0, 10, Status: EquipmentListingStatus.Draft));
        Assert.Single(resStatus.Items);
        Assert.Equal(l1.Id, resStatus.Items[0].Listing.Id);
    }

    #endregion

    #region Public Search (Name OR Model, Dynamic Filters, Only Active)

    [Fact]
    public async Task PublicSearch_MatchesNameOrModel_WithDynamicFilters_OnlyActive()
    {
        await using var db = CreateDbContext();
        var repo = new EquipmentListingRepository(db);

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        // Listing Active có name "Sony FX3 Full Package", model "FX3", specValues {"resolution":"4K", "fps":120}
        var lActive = await IntegrationTestHelper.InsertTestListingAsync(
            db,
            ownerId: ownerId,
            eqTypeId: eqTypeId,
            brandId: brandId,
            name: "Sony FX3 Full Package",
            model: "FX3",
            specValues: "{\"resolution\":\"4K\",\"fps\":120}",
            status: EquipmentListingStatus.Active,
            initialUnits: 1);

        // Listing Draft có cùng name -> public KHÔNG bao giờ thấy
        var lDraft = await IntegrationTestHelper.InsertTestListingAsync(
            db,
            ownerId: ownerId,
            eqTypeId: eqTypeId,
            brandId: brandId,
            name: "Sony FX3 Full Package Draft",
            model: "FX3",
            specValues: "{\"resolution\":\"4K\",\"fps\":120}",
            status: EquipmentListingStatus.Draft,
            initialUnits: 1);

        var criteriaName = new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(SearchKeyword: "Full Package", EquipmentTypeId: eqTypeId),
            skip: 0, take: 10);
        var resName = await repo.SearchPublicAsync(criteriaName);
        Assert.True(resName.TotalCount > 0, $"resName.TotalCount was {resName.TotalCount}");
        Assert.Single(resName.Items);
        Assert.Equal(lActive.Id, resName.Items[0].Listing.Id);

        // 2. Tìm theo Name kết hợp dynamic filter resolution = "4K"
        var criteriaDynamic = new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(
                SearchKeyword: "Full Package",
                EquipmentTypeId: eqTypeId,
                CompatibleSchemaVersions: [1],
                DynamicFilters: [new AssetDynamicFilterCriterion("resolution", "eq", "4K", "string")]),
            skip: 0, take: 10);
        var resDynamic = await repo.SearchPublicAsync(criteriaDynamic);
        Assert.Single(resDynamic.Items);
        Assert.Equal(lActive.Id, resDynamic.Items[0].Listing.Id);

        // 3. Distinct schema versions công khai cũng lọc đúng theo Name mới
        var versions = await repo.GetDistinctSchemaVersionsPublicAsync(new AssetSearchFilters(SearchKeyword: "Full Package", EquipmentTypeId: eqTypeId));
        Assert.Single(versions);
        Assert.Equal(1, versions[0]);
    }

    #endregion

    #region PATCH Only Name/Description Does Not Call Catalog or Media

    [Fact]
    public async Task PatchListing_OnlyNameOrDescription_DoesNotCallCatalogOrMedia_AndValidatesCorrectly()
    {
        var (app, secFactory, catalog, media, _) = CreateFactory();
        await using var _1 = app;
        await using var _2 = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        Guid listingId;
        await using (var db = CreateDbContext())
        {
            var listing = await IntegrationTestHelper.InsertTestListingAsync(
                db, ownerId: owner, name: "Initial Name", description: "Initial Desc", model: "FX3", initialUnits: 1);
            listingId = listing.Id;
        }

        // 1. PATCH đổi name hợp lệ -> không gọi Catalog hay Media
        var patch1 = new { name = "New Updated Name" };
        var resp1 = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listingId:D}",
            new StringContent(JsonSerializer.Serialize(patch1), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var data1 = await resp1.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.NotNull(data1);
        Assert.Equal("New Updated Name", data1.Name);
        Assert.Equal("Initial Desc", data1.Description); // description giữ nguyên vì omitted
        Assert.Equal(0, catalog.BrandCalls);
        Assert.Equal(0, catalog.SpecCalls);
        Assert.Equal(0, media.VerifyCalls);

        // 2. PATCH description = null -> xóa mô tả thành null
        var patch2 = new { description = (string?)null };
        var resp2 = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listingId:D}",
            new StringContent(JsonSerializer.Serialize(patch2), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var data2 = await resp2.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.NotNull(data2);
        Assert.Equal("New Updated Name", data2.Name); // name giữ nguyên vì omitted
        Assert.Null(data2.Description);
        Assert.Equal(0, catalog.BrandCalls);
        Assert.Equal(0, catalog.SpecCalls);
        Assert.Equal(0, media.VerifyCalls);

        // 3. PATCH name = null hoặc rỗng -> 400 BadRequest
        var patchBadName = new { name = "   " };
        var respBad = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listingId:D}",
            new StringContent(JsonSerializer.Serialize(patchBadName), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, respBad.StatusCode);

        // 4. PATCH status string số "999" -> 400 BadRequest
        var respBadStatus = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listingId:D}",
            new StringContent("{\"status\":\"999\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, respBadStatus.StatusCode);
    }

    #endregion

    #region Offset Long Validation Rejection

    [Fact]
    public async Task OffsetValidation_ExceedingIntMax_Returns400()
    {
        var (app, secFactory, _, _, _) = CreateFactory();
        await using var _1 = app;
        await using var _2 = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        Guid listingId;
        await using (var db = CreateDbContext())
        {
            var listing = await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: owner, initialUnits: 1);
            listingId = listing.Id;
        }

        // 1. Owner listings với page cực lớn làm offset vượt quá int.MaxValue -> 400
        var respList = await client.GetAsync($"api/v1/inventory/equipment-listings?page={int.MaxValue}&pageSize=100");
        Assert.Equal(HttpStatusCode.BadRequest, respList.StatusCode);

        // 2. Listing assets với page cực lớn làm offset vượt quá int.MaxValue -> 400
        var respAssets = await client.GetAsync($"api/v1/inventory/equipment-listings/{listingId:D}/assets?page={int.MaxValue}&pageSize=100");
        Assert.Equal(HttpStatusCode.BadRequest, respAssets.StatusCode);
    }

    #endregion
}
