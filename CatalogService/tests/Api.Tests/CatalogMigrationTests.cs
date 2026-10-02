using System.Diagnostics.CodeAnalysis;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
[Collection(PostgreSqlTestGroup.Name)]
public sealed class CatalogMigrationTests
{
    private const string BaseMasterConnectionString = "Host=localhost;Port=5436;Database=postgres;Username=postgres;Password=postgres;Include Error Detail=true";

    [Fact]
    public async Task UpgradeMigration_FromHistorical_PreservesDataAndDropsSupplierColumns()
    {
        string dbName = $"catalog_mig_upgrade_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(dbName);

        try
        {
            string dbConnString = GetConnectionString(dbName);
            using var context = CreateContext(dbConnString);

            var migrator = context.GetService<IMigrator>();

            // 1. Áp dụng 2 migration lịch sử
            await migrator.MigrateAsync("20261001080359_InitCategoryTable");

            // 2. Seed dữ liệu cũ với đủ 3 status và các supplier columns
            Guid categoryId = Guid.NewGuid();
            Guid orgId1 = Guid.NewGuid();
            Guid orgId2 = Guid.NewGuid();
            Guid draftId = Guid.NewGuid();
            Guid publishedId = Guid.NewGuid();
            Guid hiddenId = Guid.NewGuid();
            Guid media1Id = Guid.NewGuid();
            Guid media2Id = Guid.NewGuid();
            Guid mediaAsset1 = Guid.NewGuid();
            Guid mediaAsset2 = Guid.NewGuid();

            DateTime now = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
            DateTime tPublished = now.AddHours(-2);
            DateTime tUpdated = now.AddHours(-1);

            await using (var seedConn = new NpgsqlConnection(dbConnString))
            {
                await seedConn.OpenAsync();

                // Insert Category
                await using var catCmd = new NpgsqlCommand(
                    "INSERT INTO catalog.\"Categories\" (\"Id\", \"Name\", \"Slug\", \"IsActive\", \"CreatedAtUtc\") VALUES (@id, 'Camera', 'camera', TRUE, @now);",
                    seedConn);
                catCmd.Parameters.AddWithValue("id", categoryId);
                catCmd.Parameters.AddWithValue("now", now);
                await catCmd.ExecuteNonQueryAsync();

                // Insert Draft
                await using var draftCmd = new NpgsqlCommand(
                    """
                    INSERT INTO catalog."Equipments"
                    ("Id", "OrganizationId", "CategoryId", "Code", "Name", "Brand", "Description", "DailyPrice", "RegionId", "Status", "Specifications", "CreatedAtUtc", "UpdatedAtUtc", "PublishedAtUtc")
                    VALUES
                    (@id, @orgId, @catId, 'EQ-DRAFT', 'Draft Eq', 'Sony', 'Draft Desc', 100000, @regId, 'Draft', '{"weight": 500}'::jsonb, @tCreated, NULL, NULL);
                    """, seedConn);
                draftCmd.Parameters.AddWithValue("id", draftId);
                draftCmd.Parameters.AddWithValue("orgId", orgId1);
                draftCmd.Parameters.AddWithValue("catId", categoryId);
                draftCmd.Parameters.AddWithValue("regId", Guid.NewGuid());
                draftCmd.Parameters.AddWithValue("tCreated", now);
                await draftCmd.ExecuteNonQueryAsync();

                // Insert Published
                await using var pubCmd = new NpgsqlCommand(
                    """
                    INSERT INTO catalog."Equipments"
                    ("Id", "OrganizationId", "CategoryId", "Code", "Name", "Brand", "Description", "DailyPrice", "RegionId", "Status", "Specifications", "CreatedAtUtc", "UpdatedAtUtc", "PublishedAtUtc")
                    VALUES
                    (@id, @orgId, @catId, 'EQ-PUB', 'Published Eq', 'Canon', 'Pub Desc', 200000, @regId, 'Published', '{"sensor": "fullframe"}'::jsonb, @tCreated, @tUpdated, @tPublished);
                    """, seedConn);
                pubCmd.Parameters.AddWithValue("id", publishedId);
                pubCmd.Parameters.AddWithValue("orgId", orgId1);
                pubCmd.Parameters.AddWithValue("catId", categoryId);
                pubCmd.Parameters.AddWithValue("regId", Guid.NewGuid());
                pubCmd.Parameters.AddWithValue("tCreated", now.AddDays(-1));
                pubCmd.Parameters.AddWithValue("tUpdated", tUpdated);
                pubCmd.Parameters.AddWithValue("tPublished", tPublished);
                await pubCmd.ExecuteNonQueryAsync();

                // Insert Hidden
                await using var hiddenCmd = new NpgsqlCommand(
                    """
                    INSERT INTO catalog."Equipments"
                    ("Id", "OrganizationId", "CategoryId", "Code", "Name", "Brand", "Description", "DailyPrice", "RegionId", "Status", "Specifications", "CreatedAtUtc", "UpdatedAtUtc", "PublishedAtUtc")
                    VALUES
                    (@id, @orgId, @catId, 'EQ-HIDDEN', 'Hidden Eq', 'Nikon', 'Hidden Desc', 300000, @regId, 'Hidden', '{}'::jsonb, @tCreated, @tUpdated, @tPublished);
                    """, seedConn);
                hiddenCmd.Parameters.AddWithValue("id", hiddenId);
                hiddenCmd.Parameters.AddWithValue("orgId", orgId2);
                hiddenCmd.Parameters.AddWithValue("catId", categoryId);
                hiddenCmd.Parameters.AddWithValue("regId", Guid.NewGuid());
                hiddenCmd.Parameters.AddWithValue("tCreated", now.AddDays(-2));
                hiddenCmd.Parameters.AddWithValue("tUpdated", tUpdated);
                hiddenCmd.Parameters.AddWithValue("tPublished", tPublished);
                await hiddenCmd.ExecuteNonQueryAsync();

                // Insert Media
                await using var mediaCmd = new NpgsqlCommand(
                    """
                    INSERT INTO catalog."EquipmentMedia" ("Id", "EquipmentId", "MediaId", "DisplayOrder", "IsThumbnail")
                    VALUES
                    (@m1, @eqId, @mediaGuid1, 0, TRUE),
                    (@m2, @eqId, @mediaGuid2, 1, FALSE);
                    """, seedConn);
                mediaCmd.Parameters.AddWithValue("m1", media1Id);
                mediaCmd.Parameters.AddWithValue("m2", media2Id);
                mediaCmd.Parameters.AddWithValue("eqId", publishedId);
                mediaCmd.Parameters.AddWithValue("mediaGuid1", mediaAsset1);
                mediaCmd.Parameters.AddWithValue("mediaGuid2", mediaAsset2);
                await mediaCmd.ExecuteNonQueryAsync();
            }

            // 3. Chạy forward migration RefactorEquipmentToEquipmentType
            await context.Database.MigrateAsync();

            // 4. Kiểm tra schema và dữ liệu sau migration
            await using (var verifyConn = new NpgsqlConnection(dbConnString))
            {
                await verifyConn.OpenAsync();

                // Kiểm tra bảng cũ không còn tồn tại
                await using var oldTableCmd = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM information_schema.tables
                    WHERE table_schema = 'catalog' AND table_name IN ('Equipments', 'EquipmentMedia');
                    """, verifyConn);
                var oldTableCount = (long)(await oldTableCmd.ExecuteScalarAsync())!;
                Assert.Equal(0, oldTableCount);

                // Kiểm tra bảng mới tồn tại
                await using var newTableCmd = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM information_schema.tables
                    WHERE table_schema = 'catalog' AND table_name IN ('EquipmentTypes', 'EquipmentTypeMedia');
                    """, verifyConn);
                var newTableCount = (long)(await newTableCmd.ExecuteScalarAsync())!;
                Assert.Equal(2, newTableCount);

                // Kiểm tra các supplier columns đã bị xóa hoàn toàn
                await using var droppedColsCmd = new NpgsqlCommand(
                    """
                    SELECT column_name FROM information_schema.columns
                    WHERE table_schema = 'catalog' AND table_name = 'EquipmentTypes'
                      AND column_name IN ('Brand', 'DailyPrice', 'RegionId', 'Specifications', 'CreatedAtUtc', 'UpdatedAtUtc', 'PublishedAtUtc');
                    """, verifyConn);
                await using var reader = await droppedColsCmd.ExecuteReaderAsync();
                Assert.False(await reader.ReadAsync(), "Các supplier columns hoặc timestamp cũ vẫn còn tồn tại!");
                await reader.CloseAsync();

                // Kiểm tra các canonical columns mới tồn tại
                await using var canonicalColsCmd = new NpgsqlCommand(
                    """
                    SELECT column_name FROM information_schema.columns
                    WHERE table_schema = 'catalog' AND table_name = 'EquipmentTypes'
                      AND column_name IN ('CreatedAt', 'UpdatedAt', 'PublishedAt');
                    """, verifyConn);
                var colList = new List<string>();
                await using (var colReader = await canonicalColsCmd.ExecuteReaderAsync())
                {
                    while (await colReader.ReadAsync())
                    {
                        colList.Add(colReader.GetString(0));
                    }
                }
                Assert.Equal(3, colList.Count);

                // Kiểm tra dữ liệu: Hidden -> Disabled, các trường canonical được bảo toàn
                await using var hiddenVerifyCmd = new NpgsqlCommand(
                    "SELECT \"Code\", \"Name\", \"Description\", \"Status\", \"PublishedAt\" FROM catalog.\"EquipmentTypes\" WHERE \"Id\" = @id;",
                    verifyConn);
                hiddenVerifyCmd.Parameters.AddWithValue("id", hiddenId);
                await using (var hReader = await hiddenVerifyCmd.ExecuteReaderAsync())
                {
                    Assert.True(await hReader.ReadAsync());
                    Assert.Equal("EQ-HIDDEN", hReader.GetString(0));
                    Assert.Equal("Hidden Eq", hReader.GetString(1));
                    Assert.Equal("Hidden Desc", hReader.GetString(2));
                    Assert.Equal("Disabled", hReader.GetString(3));
                    Assert.Equal(tPublished, hReader.GetDateTime(4));
                }

                // Kiểm tra dữ liệu: Published giữ nguyên
                await using var pubVerifyCmd = new NpgsqlCommand(
                    "SELECT \"Code\", \"Name\", \"Status\", \"PublishedAt\" FROM catalog.\"EquipmentTypes\" WHERE \"Id\" = @id;",
                    verifyConn);
                pubVerifyCmd.Parameters.AddWithValue("id", publishedId);
                await using (var pReader = await pubVerifyCmd.ExecuteReaderAsync())
                {
                    Assert.True(await pReader.ReadAsync());
                    Assert.Equal("EQ-PUB", pReader.GetString(0));
                    Assert.Equal("Published Eq", pReader.GetString(1));
                    Assert.Equal("Published", pReader.GetString(2));
                    Assert.Equal(tPublished, pReader.GetDateTime(3));
                }

                // Kiểm tra media identities/metadata
                await using var mediaDetailsCmd = new NpgsqlCommand(
                    "SELECT \"Id\", \"EquipmentTypeId\", \"MediaId\", \"DisplayOrder\", \"IsThumbnail\" FROM catalog.\"EquipmentTypeMedia\" WHERE \"EquipmentTypeId\" = @id ORDER BY \"DisplayOrder\";",
                    verifyConn);
                mediaDetailsCmd.Parameters.AddWithValue("id", publishedId);
                await using (var mReader = await mediaDetailsCmd.ExecuteReaderAsync())
                {
                    Assert.True(await mReader.ReadAsync());
                    Assert.Equal(media1Id, mReader.GetGuid(0));
                    Assert.Equal(publishedId, mReader.GetGuid(1));
                    Assert.Equal(mediaAsset1, mReader.GetGuid(2));
                    Assert.Equal(0, mReader.GetInt32(3));
                    Assert.True(mReader.GetBoolean(4));

                    Assert.True(await mReader.ReadAsync());
                    Assert.Equal(media2Id, mReader.GetGuid(0));
                    Assert.Equal(publishedId, mReader.GetGuid(1));
                    Assert.Equal(mediaAsset2, mReader.GetGuid(2));
                    Assert.Equal(1, mReader.GetInt32(3));
                    Assert.False(mReader.GetBoolean(4));
                }

                // Kiểm tra Foreign Keys
                await using var fkCmd = new NpgsqlCommand(
                    """
                    SELECT constraint_name FROM information_schema.table_constraints
                    WHERE table_schema = 'catalog' AND constraint_type = 'FOREIGN KEY'
                    ORDER BY constraint_name;
                    """, verifyConn);
                var fks = new List<string>();
                await using (var fkReader = await fkCmd.ExecuteReaderAsync())
                {
                    while (await fkReader.ReadAsync())
                    {
                        fks.Add(fkReader.GetString(0));
                    }
                }
                Assert.Contains("FK_EquipmentTypeCategories_Categories_CategoryId", fks);
                Assert.Contains("FK_EquipmentTypeCategories_EquipmentTypes_EquipmentTypeId", fks);
                Assert.Contains("FK_EquipmentTypeMedia_EquipmentTypes_EquipmentTypeId", fks);
                // Kiểm tra Indexes
                await using var idxCmd = new NpgsqlCommand(
                    """
                    SELECT indexname FROM pg_indexes
                    WHERE schemaname = 'catalog'
                    ORDER BY indexname;
                    """, verifyConn);
                var indexes = new List<string>();
                await using (var idxReader = await idxCmd.ExecuteReaderAsync())
                {
                    while (await idxReader.ReadAsync())
                    {
                        indexes.Add(idxReader.GetString(0));
                    }
                }
                Assert.Contains("IX_EquipmentTypes_Code", indexes);
                Assert.Contains("IX_EquipmentTypeCategories_CategoryId", indexes);
                Assert.Contains("IX_EquipmentTypeMedia_EquipmentTypeId_MediaId", indexes);
                Assert.Contains("UX_EquipmentTypeMedia_EquipmentTypeId_Thumbnail", indexes);
                // Kiểm tra migration history đủ cả 3 migrations trong catalog.__EFMigrationsHistory
                await using var histCmd = new NpgsqlCommand(
                    "SELECT \"MigrationId\" FROM catalog.\"__EFMigrationsHistory\" ORDER BY \"MigrationId\";",
                    verifyConn);
                var appliedMigrations = new List<string>();
                await using (var histReader = await histCmd.ExecuteReaderAsync())
                {
                    while (await histReader.ReadAsync())
                    {
                        appliedMigrations.Add(histReader.GetString(0));
                    }
                }
                Assert.Equal(5, appliedMigrations.Count);
                Assert.Contains(appliedMigrations, m => m.EndsWith("_InitialCatalogSchema", StringComparison.Ordinal));
                Assert.Contains(appliedMigrations, m => m.EndsWith("_InitCategoryTable", StringComparison.Ordinal));
                Assert.Contains(appliedMigrations, m => m.EndsWith("_RefactorEquipmentToEquipmentType", StringComparison.Ordinal));
                Assert.Contains(appliedMigrations, m => m.EndsWith("_AddCategoryImagesAndEquipmentTypeCategories", StringComparison.Ordinal));
                Assert.Contains(appliedMigrations, m => m.EndsWith("_AddEquipmentTypeOrganizationId", StringComparison.Ordinal));

                // Kiểm tra quan hệ nhiều-nhiều được copy đầy đủ từ CategoryId cũ (draftId, publishedId, hiddenId)
                await using var checkCopyCmd = new NpgsqlCommand(
                    "SELECT count(*) FROM catalog.\"EquipmentTypeCategories\" WHERE \"CategoryId\" = @catId;",
                    verifyConn);
                checkCopyCmd.Parameters.AddWithValue("catId", categoryId);
                var copiedCount = (long)(await checkCopyCmd.ExecuteScalarAsync())!;
                Assert.Equal(3, copiedCount);
            }
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task UpgradeMigration_FailsOnDuplicateCode_WithoutAlteringSchemaOrData()
    {
        string dbName = $"catalog_mig_dup_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(dbName);

        try
        {
            string dbConnString = GetConnectionString(dbName);
            using var context = CreateContext(dbConnString);

            var migrator = context.GetService<IMigrator>();

            // 1. Áp dụng 2 migration lịch sử
            await migrator.MigrateAsync("20261001080359_InitCategoryTable");

            // 2. Seed 2 equipments có cùng Code dưới 2 Organization khác nhau
            Guid categoryId = Guid.NewGuid();
            Guid eq1Id = Guid.NewGuid();
            Guid eq2Id = Guid.NewGuid();
            Guid org1 = Guid.NewGuid();
            Guid org2 = Guid.NewGuid();

            await using (var seedConn = new NpgsqlConnection(dbConnString))
            {
                await seedConn.OpenAsync();

                await using var catCmd = new NpgsqlCommand(
                    "INSERT INTO catalog.\"Categories\" (\"Id\", \"Name\", \"Slug\", \"IsActive\", \"CreatedAtUtc\") VALUES (@id, 'Lighting', 'lighting', TRUE, NOW());",
                    seedConn);
                catCmd.Parameters.AddWithValue("id", categoryId);
                await catCmd.ExecuteNonQueryAsync();

                const string insertSql = """
                    INSERT INTO catalog."Equipments"
                    ("Id", "OrganizationId", "CategoryId", "Code", "Name", "Brand", "Description", "DailyPrice", "RegionId", "Status", "Specifications", "CreatedAtUtc")
                    VALUES
                    (@id, @orgId, @catId, 'DUPLICATE-CODE', 'Eq Name', 'Brand', 'Desc', 100000, @regId, 'Draft', '{}'::jsonb, NOW());
                    """;

                await using var cmd1 = new NpgsqlCommand(insertSql, seedConn);
                cmd1.Parameters.AddWithValue("id", eq1Id);
                cmd1.Parameters.AddWithValue("orgId", org1);
                cmd1.Parameters.AddWithValue("catId", categoryId);
                cmd1.Parameters.AddWithValue("regId", Guid.NewGuid());
                await cmd1.ExecuteNonQueryAsync();

                await using var cmd2 = new NpgsqlCommand(insertSql, seedConn);
                cmd2.Parameters.AddWithValue("id", eq2Id);
                cmd2.Parameters.AddWithValue("orgId", org2);
                cmd2.Parameters.AddWithValue("catId", categoryId);
                cmd2.Parameters.AddWithValue("regId", Guid.NewGuid());
                await cmd2.ExecuteNonQueryAsync();
            }

            // 3. Chạy forward migration phải thất bại với lỗi báo duplicate code rõ ràng
            var ex = await Assert.ThrowsAsync<PostgresException>(() => context.Database.MigrateAsync());
            Assert.Contains("duplicate Code values found", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DUPLICATE-CODE", ex.Message, StringComparison.OrdinalIgnoreCase);

            // 4. Kiểm tra schema và data không bị thay đổi (Equipments vẫn còn, EquipmentTypes không được tạo)
            await using (var verifyConn = new NpgsqlConnection(dbConnString))
            {
                await verifyConn.OpenAsync();

                await using var checkTableCmd = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM information_schema.tables
                    WHERE table_schema = 'catalog' AND table_name = 'Equipments';
                    """, verifyConn);
                var tableCount = (long)(await checkTableCmd.ExecuteScalarAsync())!;
                Assert.Equal(1, tableCount);

                await using var checkNewTableCmd = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM information_schema.tables
                    WHERE table_schema = 'catalog' AND table_name = 'EquipmentTypes';
                    """, verifyConn);
                var newTableCount = (long)(await checkNewTableCmd.ExecuteScalarAsync())!;
                Assert.Equal(0, newTableCount);

                // Dữ liệu cũ còn nguyên vẹn cả 2 rows
                await using var countRowsCmd = new NpgsqlCommand(
                    "SELECT \"Id\", \"OrganizationId\", \"Code\" FROM catalog.\"Equipments\" ORDER BY \"Id\";", verifyConn);
                var loadedRows = new List<(Guid id, Guid org, string code)>();
                await using (var r = await countRowsCmd.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync())
                    {
                        loadedRows.Add((r.GetGuid(0), r.GetGuid(1), r.GetString(2)));
                    }
                }
                Assert.Equal(2, loadedRows.Count);
                Assert.All(loadedRows, r => Assert.Equal("DUPLICATE-CODE", r.code));
            }
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task FreshDatabase_AppliesAllMigrationsSuccessfully()
    {
        string dbName = $"catalog_mig_fresh_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(dbName);

        try
        {
            string dbConnString = GetConnectionString(dbName);
            using var context = CreateContext(dbConnString);

            // Chạy toàn bộ migrations từ đầu
            await context.Database.MigrateAsync();

            Assert.False(context.Database.HasPendingModelChanges());

            // Thử chèn Category và EquipmentType trên schema mới
            var category = new Category
            {
                Id = Guid.NewGuid(),
                Name = "Guitars",
                Slug = "guitars-fresh",
                CreatedAtUtc = DateTime.UtcNow
            };
            context.Categories.Add(category);

            var equipmentType = new EquipmentType
            {
                Id = Guid.NewGuid(),
                Code = "FRESH-001",
                Name = "Fresh Guitar",
                CreatedAt = DateTime.UtcNow,
                Categories = [category]
            };
            context.EquipmentTypes.Add(equipmentType);

            await context.SaveChangesAsync();

            var loaded = await context.EquipmentTypes.FirstOrDefaultAsync(e => e.Id == equipmentType.Id);
            Assert.NotNull(loaded);
            Assert.Equal("FRESH-001", loaded.Code);
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    private static string GetConnectionString(string dbName) =>
        $"Host=localhost;Port=5436;Database={dbName};Username=postgres;Password=postgres;Include Error Detail=true";

    private static CatalogServiceDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<CatalogServiceDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable(
                    CatalogServiceDbContext.MigrationsHistoryTableName,
                    CatalogServiceDbContext.SchemaName))
            .Options;

        return new CatalogServiceDbContext(options);
    }

    private static async Task CreateDatabaseAsync(string dbName)
    {
        await using var conn = new NpgsqlConnection(BaseMasterConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\";", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string dbName)
    {
        await using var conn = new NpgsqlConnection(BaseMasterConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
