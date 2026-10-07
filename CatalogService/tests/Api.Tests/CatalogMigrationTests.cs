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

            // 1. Ãp dá»¥ng 2 migration lá»‹ch sá»­
            await migrator.MigrateAsync("20261001080359_InitCategoryTable");

            // 2. Seed dá»¯ liá»‡u cÅ© vá»›i Ä‘á»§ 3 status vÃ  cÃ¡c supplier columns
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

            // 3. Cháº¡y forward migration RefactorEquipmentToEquipmentType
            await context.Database.MigrateAsync();

            // 4. Kiá»ƒm tra schema vÃ  dá»¯ liá»‡u sau migration
            await using (var verifyConn = new NpgsqlConnection(dbConnString))
            {
                await verifyConn.OpenAsync();

                // Kiá»ƒm tra báº£ng cÅ© khÃ´ng cÃ²n tá»“n táº¡i
                await using var oldTableCmd = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM information_schema.tables
                    WHERE table_schema = 'catalog' AND table_name IN ('Equipments', 'EquipmentMedia');
                    """, verifyConn);
                var oldTableCount = (long)(await oldTableCmd.ExecuteScalarAsync())!;
                Assert.Equal(0, oldTableCount);

                // Kiá»ƒm tra báº£ng má»›i tá»“n táº¡i
                await using var newTableCmd = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM information_schema.tables
                    WHERE table_schema = 'catalog' AND table_name IN ('EquipmentTypes', 'EquipmentTypeMedia');
                    """, verifyConn);
                var newTableCount = (long)(await newTableCmd.ExecuteScalarAsync())!;
                Assert.Equal(2, newTableCount);

                // Kiá»ƒm tra cÃ¡c supplier columns Ä‘Ã£ bá»‹ xÃ³a hoÃ n toÃ n
                await using var droppedColsCmd = new NpgsqlCommand(
                    """
                    SELECT column_name FROM information_schema.columns
                    WHERE table_schema = 'catalog' AND table_name = 'EquipmentTypes'
                      AND column_name IN ('Brand', 'DailyPrice', 'RegionId', 'Specifications', 'CreatedAtUtc', 'UpdatedAtUtc', 'PublishedAtUtc', 'OrganizationId');
                    """, verifyConn);
                await using var reader = await droppedColsCmd.ExecuteReaderAsync();
                Assert.False(await reader.ReadAsync(), "CÃ¡c supplier columns hoáº·c timestamp cÅ© váº«n cÃ²n tá»“n táº¡i!");
                await reader.CloseAsync();

                // Kiá»ƒm tra cÃ¡c canonical columns má»›i tá»“n táº¡i
                await using var canonicalColsCmd = new NpgsqlCommand(
                    """
                    SELECT column_name FROM information_schema.columns
                    WHERE table_schema = 'catalog' AND table_name = 'EquipmentTypes'
                      AND column_name IN ('CreatedAt', 'UpdatedAt', 'PublishedAt', 'CurrentSchemaVersion');
                    """, verifyConn);
                var colList = new List<string>();
                await using (var colReader = await canonicalColsCmd.ExecuteReaderAsync())
                {
                    while (await colReader.ReadAsync())
                    {
                        colList.Add(colReader.GetString(0));
                    }
                }
                Assert.Equal(4, colList.Count);

                // Kiá»ƒm tra dá»¯ liá»‡u: Hidden -> Disabled, cÃ¡c trÆ°á»ng canonical Ä‘Æ°á»£c báº£o toÃ n
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

                // Kiá»ƒm tra dá»¯ liá»‡u: Published giá»¯ nguyÃªn
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

                // Kiá»ƒm tra media identities/metadata
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

                // Kiá»ƒm tra Foreign Keys
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
                // Kiá»ƒm tra Indexes
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
                // Kiá»ƒm tra migration history Ä‘á»§ cáº£ 3 migrations trong catalog.__EFMigrationsHistory
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

                Assert.Equal(10, appliedMigrations.Count);

                Assert.Contains(
                    appliedMigrations,
                    m => m.EndsWith("_InitialCatalogSchema", StringComparison.Ordinal));

                Assert.Contains(
                    appliedMigrations,
                    m => m.EndsWith("_InitCategoryTable", StringComparison.Ordinal));

                Assert.Contains(
                    appliedMigrations,
                    m => m.EndsWith("_RefactorEquipmentToEquipmentType", StringComparison.Ordinal));

                Assert.Contains(
                    appliedMigrations,
                    m => m.EndsWith("_AddCategoryImagesAndEquipmentTypeCategories", StringComparison.Ordinal));

                Assert.Contains(
                    appliedMigrations,
                    m => m.EndsWith("_AddEquipmentTypeOrganizationId", StringComparison.Ordinal));

                Assert.Contains(
                    appliedMigrations,
                    m => m.EndsWith("_RefactorToCanonicalCatalog", StringComparison.Ordinal));
                Assert.Contains(appliedMigrations, m => m.EndsWith("_AddEquipmentTypeSpecSchemaAndDropOrganizationId", StringComparison.Ordinal));
                Assert.Contains(appliedMigrations, m => m.EndsWith("_AddBrands", StringComparison.Ordinal));
                Assert.Contains(appliedMigrations, m => m.EndsWith("_CanonicalRulesAndSchemaLifecycle", StringComparison.Ordinal));
                Assert.Contains(appliedMigrations, m => m.EndsWith("_UseEquipmentTypeSpecSchemasOnly", StringComparison.Ordinal));
                // Kiá»ƒm tra quan há»‡ nhiá»u-nhiá»u Ä‘Æ°á»£c copy Ä‘áº§y Ä‘á»§ tá»« CategoryId cÅ© (draftId, publishedId, hiddenId)
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

            // 1. Ãp dá»¥ng 2 migration lá»‹ch sá»­
            await migrator.MigrateAsync("20261001080359_InitCategoryTable");

            // 2. Seed 2 equipments cÃ³ cÃ¹ng Code dÆ°á»›i 2 Organization khÃ¡c nhau
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

            // 3. Cháº¡y forward migration pháº£i tháº¥t báº¡i vá»›i lá»—i bÃ¡o duplicate code rÃµ rÃ ng
            var ex = await Assert.ThrowsAsync<PostgresException>(() => context.Database.MigrateAsync());
            Assert.Contains("duplicate Code values found", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DUPLICATE-CODE", ex.Message, StringComparison.OrdinalIgnoreCase);

            // 4. Kiá»ƒm tra schema vÃ  data khÃ´ng bá»‹ thay Ä‘á»•i (Equipments váº«n cÃ²n, EquipmentTypes khÃ´ng Ä‘Æ°á»£c táº¡o)
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

                // Dá»¯ liá»‡u cÅ© cÃ²n nguyÃªn váº¹n cáº£ 2 rows
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

            // Cháº¡y toÃ n bá»™ migrations tá»« Ä‘áº§u
            await context.Database.MigrateAsync();

            Assert.False(context.Database.HasPendingModelChanges());

            // Thá»­ chÃ¨n Category vÃ  EquipmentType trÃªn schema má»›i
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

            var brand = new Brand
            {
                Id = Guid.NewGuid(),
                Code = "SONY",
                Name = "Sony Corporation",
                Slug = "sony",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            context.Brands.Add(brand);

            await context.SaveChangesAsync();

            var loaded = await context.EquipmentTypes.FirstOrDefaultAsync(e => e.Id == equipmentType.Id);
            Assert.NotNull(loaded);
            Assert.Equal("FRESH-001", loaded.Code);
            var loadedBrand = await context.Brands.FirstOrDefaultAsync(b => b.Id == brand.Id);
            Assert.NotNull(loadedBrand);
            Assert.Equal("SONY", loadedBrand.Code);
            Assert.Equal("sony", loadedBrand.Slug);
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task UpgradeMigration_FromOwnership_BackfillsSpecSchema_AndPreservesCategoryAndMedia()
    {
        string dbName = $"catalog_mig_ownership_upgrade_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(dbName);

        try
        {
            string dbConnString = GetConnectionString(dbName);
            using var context = CreateContext(dbConnString);
            var migrator = context.GetService<IMigrator>();

            // 1. Ãp dá»¥ng Ä‘áº¿n migration ownership gáº§n nháº¥t
            await migrator.MigrateAsync("20261002100634_AddEquipmentTypeOrganizationId");

            Guid cat1Id = Guid.NewGuid();
            Guid cat2Id = Guid.NewGuid();
            Guid org1 = Guid.NewGuid();
            Guid org2 = Guid.NewGuid();

            Guid draftId = Guid.NewGuid();
            Guid pubId = Guid.NewGuid();
            Guid disId = Guid.NewGuid();

            Guid media1Id = Guid.NewGuid();
            Guid media2Id = Guid.NewGuid();

            DateTime tCreated = new(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc);
            DateTime tPublished = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
            DateTime tUpdated = new(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc);

            // Seed categories, equipment types vá»›i organization_id vÃ  media
            await using (var seedConn = new NpgsqlConnection(dbConnString))
            {
                await seedConn.OpenAsync();

                // Categories
                await using var catCmd = new NpgsqlCommand(
                    """
                    INSERT INTO catalog."Categories" ("Id", "Name", "Slug", "IsActive", "CreatedAtUtc") VALUES
                    (@cat1, 'Camera Pro', 'camera-pro', TRUE, NOW()),
                    (@cat2, 'Lenses Pro', 'lenses-pro', TRUE, NOW());
                    """, seedConn);
                catCmd.Parameters.AddWithValue("cat1", cat1Id);
                catCmd.Parameters.AddWithValue("cat2", cat2Id);
                await catCmd.ExecuteNonQueryAsync();

                // EquipmentTypes cÃ³ OrganizationId
                await using var eqCmd = new NpgsqlCommand(
                    """
                    INSERT INTO catalog."EquipmentTypes"
                    ("Id", "Code", "Name", "Description", "Status", "CreatedAt", "UpdatedAt", "PublishedAt", "OrganizationId") VALUES
                    (@draftId, 'EQ-DRAFT', 'Draft Eq', 'Desc Draft', 'Draft', @tCreated, NULL, NULL, @org1),
                    (@pubId, 'EQ-PUB', 'Published Eq', 'Desc Pub', 'Published', @tCreated, @tUpdated, @tPublished, @org2),
                    (@disId, 'EQ-DIS', 'Disabled Eq', 'Desc Dis', 'Disabled', @tCreated, @tUpdated, @tPublished, NULL);
                    """, seedConn);
                eqCmd.Parameters.AddWithValue("draftId", draftId);
                eqCmd.Parameters.AddWithValue("pubId", pubId);
                eqCmd.Parameters.AddWithValue("disId", disId);
                eqCmd.Parameters.AddWithValue("org1", org1);
                eqCmd.Parameters.AddWithValue("org2", org2);
                eqCmd.Parameters.AddWithValue("tCreated", tCreated);
                eqCmd.Parameters.AddWithValue("tPublished", tPublished);
                eqCmd.Parameters.AddWithValue("tUpdated", tUpdated);
                await eqCmd.ExecuteNonQueryAsync();

                // Join Categories
                await using var joinCmd = new NpgsqlCommand(
                    """
                    INSERT INTO catalog."EquipmentTypeCategories" ("EquipmentTypeId", "CategoryId") VALUES
                    (@draftId, @cat1),
                    (@draftId, @cat2),
                    (@pubId, @cat1),
                    (@disId, @cat2);
                    """, seedConn);
                joinCmd.Parameters.AddWithValue("draftId", draftId);
                joinCmd.Parameters.AddWithValue("pubId", pubId);
                joinCmd.Parameters.AddWithValue("disId", disId);
                joinCmd.Parameters.AddWithValue("cat1", cat1Id);
                joinCmd.Parameters.AddWithValue("cat2", cat2Id);
                await joinCmd.ExecuteNonQueryAsync();

                // Media
                await using var mediaCmd = new NpgsqlCommand(
                    """
                    INSERT INTO catalog."EquipmentTypeMedia" ("Id", "EquipmentTypeId", "MediaId", "DisplayOrder", "IsThumbnail") VALUES
                    (gen_random_uuid(), @draftId, @m1, 0, TRUE),
                    (gen_random_uuid(), @pubId, @m2, 0, TRUE);
                    """, seedConn);
                mediaCmd.Parameters.AddWithValue("draftId", draftId);
                mediaCmd.Parameters.AddWithValue("pubId", pubId);
                mediaCmd.Parameters.AddWithValue("m1", media1Id);
                mediaCmd.Parameters.AddWithValue("m2", media2Id);
                await mediaCmd.ExecuteNonQueryAsync();
            }

            // 2. Cháº¡y upgrade migration lÃªn má»›i nháº¥t
            await context.Database.MigrateAsync();

            Assert.False(context.Database.HasPendingModelChanges());

            // 3. XÃ¡c thá»±c cÆ¡ sá»Ÿ dá»¯ liá»‡u sau migration
            await using (var verifyConn = new NpgsqlConnection(dbConnString))
            {
                await verifyConn.OpenAsync();

                // XÃ¡c nháº­n cá»™t OrganizationId Ä‘Ã£ bá»‹ drop
                await using var orgColCmd = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM information_schema.columns
                    WHERE table_schema = 'catalog' AND table_name = 'EquipmentTypes' AND column_name = 'OrganizationId';
                    """, verifyConn);
                long orgColCount = (long)(await orgColCmd.ExecuteScalarAsync())!;
                Assert.Equal(0, orgColCount);

                // XÃ¡c nháº­n index OrganizationId Ä‘Ã£ bá»‹ drop
                await using var orgIdxCmd = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM pg_indexes
                    WHERE schemaname = 'catalog' AND tablename = 'EquipmentTypes' AND indexname = 'IX_EquipmentTypes_OrganizationId';
                    """, verifyConn);
                long orgIdxCount = (long)(await orgIdxCmd.ExecuteScalarAsync())!;
                Assert.Equal(0, orgIdxCount);

                // XÃ¡c nháº­n toÃ n bá»™ 3 báº£n ghi Ä‘á»u cÃ³ CurrentSchemaVersion = 1 vÃ  giá»¯ nguyÃªn dá»¯ liá»‡u gá»‘c
                await using var eqVerifyCmd = new NpgsqlCommand(
                    """
                    SELECT "Id", "Code", "Name", "Status", "CurrentSchemaVersion", "CreatedAt", "PublishedAt"
                    FROM catalog."EquipmentTypes" ORDER BY "Code";
                    """, verifyConn);
                await using (var reader = await eqVerifyCmd.ExecuteReaderAsync())
                {
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal(disId, reader.GetGuid(0));
                    Assert.Equal("EQ-DIS", reader.GetString(1));
                    Assert.Equal("Disabled", reader.GetString(3));
                    Assert.Equal(1, reader.GetInt32(4));

                    Assert.True(await reader.ReadAsync());
                    Assert.Equal(draftId, reader.GetGuid(0));
                    Assert.Equal("EQ-DRAFT", reader.GetString(1));
                    Assert.Equal("Draft", reader.GetString(3));
                    Assert.Equal(1, reader.GetInt32(4));

                    Assert.True(await reader.ReadAsync());
                    Assert.Equal(pubId, reader.GetGuid(0));
                    Assert.Equal("EQ-PUB", reader.GetString(1));
                    Assert.Equal("Published", reader.GetString(3));
                    Assert.Equal(1, reader.GetInt32(4));
                    Assert.False(await reader.ReadAsync());
                }

                // XÃ¡c nháº­n backfill spec schemas v1: Ä‘Ãºng 3 báº£n ghi, definition = {"fields": []}, published_at NOT NULL
                await using var schemaVerifyCmd = new NpgsqlCommand(
                    """
                    SELECT "EquipmentTypeId", "Version", "Definition", "PublishedAt"
                    FROM catalog."EquipmentTypeSpecSchemas" ORDER BY "Version";
                    """, verifyConn);
                await using (var sReader = await schemaVerifyCmd.ExecuteReaderAsync())
                {
                    int schemaCount = 0;
                    while (await sReader.ReadAsync())
                    {
                        schemaCount++;
                        Assert.Equal(1, sReader.GetInt32(1));
                        string defJson = sReader.GetString(2);
                        Assert.Contains("\"fields\"", defJson);
                        Assert.False(await sReader.IsDBNullAsync(3)); // PublishedAt NOT NULL
                    }
                    Assert.Equal(3, schemaCount);
                }

                // XÃ¡c nháº­n Categories Ä‘Æ°á»£c báº£o toÃ n nguyÃªn váº¹n
                await using var catVerifyCmd = new NpgsqlCommand(
                    """SELECT count(*) FROM catalog."EquipmentTypeCategories";""", verifyConn);
                Assert.Equal(4, (long)(await catVerifyCmd.ExecuteScalarAsync())!);

                // XÃ¡c nháº­n Media Ä‘Æ°á»£c báº£o toÃ n nguyÃªn váº¹n
                await using var mediaVerifyCmd = new NpgsqlCommand(
                    """SELECT count(*) FROM catalog."EquipmentTypeMedia";""", verifyConn);
                Assert.Equal(2, (long)(await mediaVerifyCmd.ExecuteScalarAsync())!);
            }

            // 4. XÃ¡c nháº­n Down migration nÃ©m NotSupportedException
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                migrator.MigrateAsync("20261002100634_AddEquipmentTypeOrganizationId"));
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task UpgradeMigration_From94321a8Fixture_WithoutActiveSchemaVersionId_UpgradesSuccessfully()
    {
        string dbName = $"catalog_mig_94321a8_no_col_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(dbName);

        try
        {
            string dbConnString = GetConnectionString(dbName);
            Guid categoryId = Guid.NewGuid();
            Guid brandId = Guid.NewGuid();
            Guid eq1Id = Guid.NewGuid();
            Guid eq2Id = Guid.NewGuid();
            Guid mediaId1 = Guid.NewGuid();
            Guid mediaId2 = Guid.NewGuid();
            DateTime now = new(2026, 10, 7, 8, 51, 0, DateTimeKind.Utc);

            // 1. Khởi tạo schema và dữ liệu fixture 94321a8 (7 migration, KHÔNG CÓ cột ActiveSchemaVersionId)
            await Setup94321a8HistoricalSchemaAsync(
                dbConnString,
                includeActiveSchemaVersionId: false,
                categoryId,
                brandId,
                eq1Id,
                eq2Id,
                mediaId1,
                mediaId2,
                now);

            // 2. Xác nhận trạng thái trước nâng cấp: 7 migrations, không có ActiveSchemaVersionId, không có bảng legacy
            await using (var preConn = new NpgsqlConnection(dbConnString))
            {
                await preConn.OpenAsync();

                await using var preHistCmd = new NpgsqlCommand(
                    "SELECT count(*) FROM catalog.\"__EFMigrationsHistory\";", preConn);
                Assert.Equal(7, (long)(await preHistCmd.ExecuteScalarAsync())!);

                await using var preColCmd = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM information_schema.columns
                    WHERE table_schema = 'catalog' AND table_name = 'EquipmentTypes' AND column_name = 'ActiveSchemaVersionId';
                    """, preConn);
                Assert.Equal(0, (long)(await preColCmd.ExecuteScalarAsync())!);

                await using var preLegacyTableCmd = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM information_schema.tables
                    WHERE table_schema = 'catalog' AND table_name IN ('SpecSchemas', 'SchemaVersions', 'SpecFields', 'SpecOptions');
                    """, preConn);
                Assert.Equal(0, (long)(await preLegacyTableCmd.ExecuteScalarAsync())!);
            }

            // 3. Thực hiện nâng cấp toàn bộ migration
            using var context = CreateContext(dbConnString);
            await context.Database.MigrateAsync();

            // 4. Xác nhận trạng thái sau nâng cấp
            await using (var postConn = new NpgsqlConnection(dbConnString))
            {
                await postConn.OpenAsync();

                // Ghi nhận đủ 10 migrations
                await using var postHistCmd = new NpgsqlCommand(
                    "SELECT \"MigrationId\" FROM catalog.\"__EFMigrationsHistory\" ORDER BY \"MigrationId\";", postConn);
                var applied = new List<string>();
                await using (var r = await postHistCmd.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync())
                    {
                        applied.Add(r.GetString(0));
                    }
                }
                Assert.Equal(10, applied.Count);
                Assert.Contains(applied, m => m.EndsWith("_RefactorToCanonicalCatalog", StringComparison.Ordinal));
                Assert.Contains(applied, m => m.EndsWith("_CanonicalRulesAndSchemaLifecycle", StringComparison.Ordinal));
                Assert.Contains(applied, m => m.EndsWith("_UseEquipmentTypeSpecSchemasOnly", StringComparison.Ordinal));

                // Cột ActiveSchemaVersionId và các bảng legacy đã bị dọn sạch
                await using var postColCmd = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM information_schema.columns
                    WHERE table_schema = 'catalog' AND table_name = 'EquipmentTypes' AND column_name = 'ActiveSchemaVersionId';
                    """, postConn);
                Assert.Equal(0, (long)(await postColCmd.ExecuteScalarAsync())!);

                await using var postLegacyTableCmd = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM information_schema.tables
                    WHERE table_schema = 'catalog' AND table_name IN ('SpecSchemas', 'SchemaVersions', 'SpecFields', 'SpecOptions');
                    """, postConn);
                Assert.Equal(0, (long)(await postLegacyTableCmd.ExecuteScalarAsync())!);

                // Xác nhận bảo toàn dữ liệu trước và sau
                // Category
                await using var catCmd = new NpgsqlCommand(
                    "SELECT \"Name\", \"Slug\", \"IsActive\" FROM catalog.\"Categories\" WHERE \"Id\" = @id;", postConn);
                catCmd.Parameters.AddWithValue("id", categoryId);
                await using (var r = await catCmd.ExecuteReaderAsync())
                {
                    Assert.True(await r.ReadAsync());
                    Assert.Equal("Âm thanh", r.GetString(0));
                    Assert.Equal("am-thanh", r.GetString(1));
                    Assert.True(r.GetBoolean(2));
                }

                // Brand
                await using var brandCmd = new NpgsqlCommand(
                    "SELECT \"Code\", \"Name\", \"Slug\", \"IsActive\" FROM catalog.\"Brands\" WHERE \"Id\" = @id;", postConn);
                brandCmd.Parameters.AddWithValue("id", brandId);
                await using (var r = await brandCmd.ExecuteReaderAsync())
                {
                    Assert.True(await r.ReadAsync());
                    Assert.Equal("SONY", r.GetString(0));
                    Assert.Equal("Sony Corporation", r.GetString(1));
                    Assert.Equal("sony", r.GetString(2));
                }

                // EquipmentTypes (eq1: Published, eq2: Draft)
                await using var eqCmd = new NpgsqlCommand(
                    "SELECT \"Id\", \"Code\", \"Name\", \"Status\", \"CurrentSchemaVersion\", \"CreatedAt\", \"PublishedAt\" FROM catalog.\"EquipmentTypes\" ORDER BY \"Code\";", postConn);
                await using (var r = await eqCmd.ExecuteReaderAsync())
                {
                    Assert.True(await r.ReadAsync());
                    Assert.Equal(eq2Id, r.GetGuid(0));
                    Assert.Equal("CAM-001", r.GetString(1));
                    Assert.Equal("Máy quay phim", r.GetString(2));
                    Assert.Equal("Draft", r.GetString(3));
                    Assert.Equal(1, r.GetInt32(4));

                    Assert.True(await r.ReadAsync());
                    Assert.Equal(eq1Id, r.GetGuid(0));
                    Assert.Equal("MIC-001", r.GetString(1));
                    Assert.Equal("Micro không dây", r.GetString(2));
                    Assert.Equal("Published", r.GetString(3));
                    Assert.Equal(1, r.GetInt32(4));
                    Assert.False(await r.IsDBNullAsync(6));
                    Assert.False(await r.ReadAsync());
                }

                // EquipmentTypeSpecSchemas (JSON definition được bảo toàn)
                await using var specCmd = new NpgsqlCommand(
                    "SELECT \"EquipmentTypeId\", \"Version\", \"Definition\" FROM catalog.\"EquipmentTypeSpecSchemas\" ORDER BY \"EquipmentTypeId\";", postConn);
                await using (var r = await specCmd.ExecuteReaderAsync())
                {
                    int count = 0;
                    while (await r.ReadAsync())
                    {
                        count++;
                        Assert.Equal(1, r.GetInt32(1));
                        string json = r.GetString(2);
                        Assert.Contains("\"fields\"", json);
                    }
                    Assert.Equal(2, count);
                }

                // Categories & Media relations
                await using var relCmd = new NpgsqlCommand(
                    "SELECT count(*) FROM catalog.\"EquipmentTypeCategories\";", postConn);
                Assert.Equal(2, (long)(await relCmd.ExecuteScalarAsync())!);

                await using var mediaCmd = new NpgsqlCommand(
                    "SELECT count(*) FROM catalog.\"EquipmentTypeMedia\";", postConn);
                Assert.Equal(2, (long)(await mediaCmd.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task UpgradeMigration_FromHistorical_WithPreexistingActiveSchemaVersionId_UpgradesSuccessfully()
    {
        string dbName = $"catalog_mig_94321a8_with_col_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(dbName);

        try
        {
            string dbConnString = GetConnectionString(dbName);
            Guid categoryId = Guid.NewGuid();
            Guid brandId = Guid.NewGuid();
            Guid eq1Id = Guid.NewGuid();
            Guid eq2Id = Guid.NewGuid();
            Guid mediaId1 = Guid.NewGuid();
            Guid mediaId2 = Guid.NewGuid();
            DateTime now = new(2026, 10, 7, 8, 51, 0, DateTimeKind.Utc);

            // 1. Khởi tạo fixture lịch sử biến thể CÓ SẴN cột ActiveSchemaVersionId
            await Setup94321a8HistoricalSchemaAsync(
                dbConnString,
                includeActiveSchemaVersionId: true,
                categoryId,
                brandId,
                eq1Id,
                eq2Id,
                mediaId1,
                mediaId2,
                now);

            // Xác nhận cột ActiveSchemaVersionId có trước khi nâng cấp
            await using (var preConn = new NpgsqlConnection(dbConnString))
            {
                await preConn.OpenAsync();
                await using var colCmd = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM information_schema.columns
                    WHERE table_schema = 'catalog' AND table_name = 'EquipmentTypes' AND column_name = 'ActiveSchemaVersionId';
                    """, preConn);
                Assert.Equal(1, (long)(await colCmd.ExecuteScalarAsync())!);
            }

            // 2. Chạy nâng cấp (IF NOT EXISTS giúp lệnh không bị lỗi trùng cột)
            using var context = CreateContext(dbConnString);
            await context.Database.MigrateAsync();

            // 3. Xác nhận sau nâng cấp
            await using (var postConn = new NpgsqlConnection(dbConnString))
            {
                await postConn.OpenAsync();

                await using var histCmd = new NpgsqlCommand(
                    "SELECT count(*) FROM catalog.\"__EFMigrationsHistory\";", postConn);
                Assert.Equal(10, (long)(await histCmd.ExecuteScalarAsync())!);

                // Cột và bảng legacy bị xóa theo đường nâng cấp
                await using var colCmd = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM information_schema.columns
                    WHERE table_schema = 'catalog' AND table_name = 'EquipmentTypes' AND column_name = 'ActiveSchemaVersionId';
                    """, postConn);
                Assert.Equal(0, (long)(await colCmd.ExecuteScalarAsync())!);

                await using var legacyTableCmd = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM information_schema.tables
                    WHERE table_schema = 'catalog' AND table_name IN ('SpecSchemas', 'SchemaVersions', 'SpecFields', 'SpecOptions');
                    """, postConn);
                Assert.Equal(0, (long)(await legacyTableCmd.ExecuteScalarAsync())!);

                // Dữ liệu EquipmentType còn nguyên vẹn
                await using var eqCmd = new NpgsqlCommand(
                    "SELECT count(*) FROM catalog.\"EquipmentTypes\";", postConn);
                Assert.Equal(2, (long)(await eqCmd.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task UpgradeMigration_IdempotentSecondRun_PreservesDataAndSchema()
    {
        string dbName = $"catalog_mig_idempotent_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(dbName);

        try
        {
            string dbConnString = GetConnectionString(dbName);
            using var context = CreateContext(dbConnString);

            // 1. Chạy migration lần 1
            await context.Database.MigrateAsync();

            // Thêm dữ liệu vào database đã nâng cấp đầy đủ
            var brand = new Brand
            {
                Id = Guid.NewGuid(),
                Code = "IDEM-01",
                Name = "Idempotent Brand",
                Slug = "idempotent-brand",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            context.Brands.Add(brand);
            await context.SaveChangesAsync();

            // 2. Chạy migration lần 2
            using var context2 = CreateContext(dbConnString);
            await context2.Database.MigrateAsync();

            // 3. Xác nhận không thay đổi dữ liệu hoặc history
            await using var verifyConn = new NpgsqlConnection(dbConnString);
            await verifyConn.OpenAsync();

            await using var histCmd = new NpgsqlCommand(
                "SELECT count(*) FROM catalog.\"__EFMigrationsHistory\";", verifyConn);
            Assert.Equal(10, (long)(await histCmd.ExecuteScalarAsync())!);

            await using var brandCmd = new NpgsqlCommand(
                "SELECT count(*) FROM catalog.\"Brands\" WHERE \"Code\" = 'IDEM-01';", verifyConn);
            Assert.Equal(1, (long)(await brandCmd.ExecuteScalarAsync())!);
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task UpgradeMigration_FailedMigration_RollsBackTransaction_AndDoesNotRecordHistory()
    {
        string dbName = $"catalog_mig_rollback_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(dbName);

        try
        {
            string dbConnString = GetConnectionString(dbName);
            using var context = CreateContext(dbConnString);
            var migrator = context.GetService<IMigrator>();

            // 1. Áp dụng đến migration InitCategoryTable
            await migrator.MigrateAsync("20261001080359_InitCategoryTable");

            // 2. Chèn dữ liệu vi phạm làm cho migration RefactorEquipmentToEquipmentType thất bại (trùng Code)
            Guid catId = Guid.NewGuid();
            DateTime now = DateTime.UtcNow;
            await using (var conn = new NpgsqlConnection(dbConnString))
            {
                await conn.OpenAsync();
                await using var catCmd = new NpgsqlCommand(
                    "INSERT INTO catalog.\"Categories\" (\"Id\", \"Name\", \"Slug\", \"IsActive\", \"CreatedAtUtc\") VALUES (@id, 'C', 'c', TRUE, @now);", conn);
                catCmd.Parameters.AddWithValue("id", catId);
                catCmd.Parameters.AddWithValue("now", now);
                await catCmd.ExecuteNonQueryAsync();

                // Chèn 2 equipment có Code trùng nhau 'DUP'
                await using var eq1Cmd = new NpgsqlCommand(
                    """
                    INSERT INTO catalog."Equipments" ("Id", "OrganizationId", "CategoryId", "Code", "Name", "Brand", "Description", "DailyPrice", "RegionId", "Status", "Specifications", "CreatedAtUtc")
                    VALUES (@id1, @org1, @catId, 'DUP', 'Eq 1', 'B', 'D', 100, @reg, 'Draft', '{}'::jsonb, @now);
                    INSERT INTO catalog."Equipments" ("Id", "OrganizationId", "CategoryId", "Code", "Name", "Brand", "Description", "DailyPrice", "RegionId", "Status", "Specifications", "CreatedAtUtc")
                    VALUES (@id2, @org2, @catId, 'DUP', 'Eq 2', 'B', 'D', 100, @reg, 'Draft', '{}'::jsonb, @now);
                    """, conn);
                eq1Cmd.Parameters.AddWithValue("id1", Guid.NewGuid());
                eq1Cmd.Parameters.AddWithValue("id2", Guid.NewGuid());
                eq1Cmd.Parameters.AddWithValue("org1", Guid.NewGuid());
                eq1Cmd.Parameters.AddWithValue("org2", Guid.NewGuid());
                eq1Cmd.Parameters.AddWithValue("catId", catId);
                eq1Cmd.Parameters.AddWithValue("reg", Guid.NewGuid());
                eq1Cmd.Parameters.AddWithValue("now", now);
                await eq1Cmd.ExecuteNonQueryAsync();
            }

            // 3. Chạy migration RefactorEquipmentToEquipmentType -> phải ném PostgresException vì trùng Code
            await Assert.ThrowsAnyAsync<PostgresException>(() =>
                migrator.MigrateAsync("20261002014206_RefactorEquipmentToEquipmentType"));

            // 4. Xác nhận transaction được rollback: RefactorEquipmentToEquipmentType KHÔNG được ghi vào history
            await using (var verifyConn = new NpgsqlConnection(dbConnString))
            {
                await verifyConn.OpenAsync();
                await using var histCmd = new NpgsqlCommand(
                    "SELECT count(*) FROM catalog.\"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%RefactorEquipmentToEquipmentType%';", verifyConn);
                Assert.Equal(0, (long)(await histCmd.ExecuteScalarAsync())!);

                // Bảng Equipments ban đầu vẫn còn nguyên
                await using var tableCmd = new NpgsqlCommand(
                    "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'catalog' AND table_name = 'Equipments';", verifyConn);
                Assert.Equal(1, (long)(await tableCmd.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    private static async Task Setup94321a8HistoricalSchemaAsync(
        string connectionString,
        bool includeActiveSchemaVersionId,
        Guid categoryId,
        Guid brandId,
        Guid eq1Id,
        Guid eq2Id,
        Guid mediaId1,
        Guid mediaId2,
        DateTime now)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        string activeColDdl = includeActiveSchemaVersionId
            ? """
              ALTER TABLE catalog."EquipmentTypes" ADD COLUMN "ActiveSchemaVersionId" uuid NULL;
              CREATE INDEX "IX_EquipmentTypes_ActiveSchemaVersionId" ON catalog."EquipmentTypes" ("ActiveSchemaVersionId");
              """
            : string.Empty;

        string ddl = $"""
            CREATE SCHEMA IF NOT EXISTS catalog;

            CREATE TABLE catalog."__EFMigrationsHistory" (
                "MigrationId" character varying(150) NOT NULL PRIMARY KEY,
                "ProductVersion" character varying(32) NOT NULL
            );

            INSERT INTO catalog."__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES
            ('20261001065210_InitialCatalogSchema', '10.0.12'),
            ('20261001080359_InitCategoryTable', '10.0.12'),
            ('20261002014206_RefactorEquipmentToEquipmentType', '10.0.12'),
            ('20261002080315_AddCategoryImagesAndEquipmentTypeCategories', '10.0.12'),
            ('20261002100634_AddEquipmentTypeOrganizationId', '10.0.12'),
            ('20261005042152_AddEquipmentTypeSpecSchemaAndDropOrganizationId', '10.0.12'),
            ('20261005083741_AddBrands', '10.0.12');

            CREATE TABLE catalog."Categories" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Name" character varying(150) NOT NULL,
                "Slug" character varying(150) NOT NULL,
                "Description" character varying(1000),
                "IsActive" boolean NOT NULL DEFAULT TRUE,
                "ImageMediaId" uuid,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                "UpdatedAtUtc" timestamp with time zone
            );
            CREATE UNIQUE INDEX "IX_Categories_Slug" ON catalog."Categories" ("Slug");

            CREATE TABLE catalog."EquipmentTypes" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Code" character varying(50) NOT NULL,
                "Name" character varying(200) NOT NULL,
                "Description" character varying(4000),
                "Status" character varying(20) NOT NULL,
                "CurrentSchemaVersion" integer,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone,
                "PublishedAt" timestamp with time zone,
                CONSTRAINT "CK_EquipmentTypes_Status_Canonical" CHECK ("Status" IN ('Draft', 'Published', 'Disabled'))
            );
            CREATE UNIQUE INDEX "IX_EquipmentTypes_Code" ON catalog."EquipmentTypes" ("Code");
            CREATE INDEX "IX_EquipmentTypes_CreatedAt" ON catalog."EquipmentTypes" ("CreatedAt");
            CREATE INDEX "IX_EquipmentTypes_PublishedAt" ON catalog."EquipmentTypes" ("PublishedAt");
            CREATE INDEX "IX_EquipmentTypes_Status" ON catalog."EquipmentTypes" ("Status");
            CREATE INDEX "IX_EquipmentTypes_Id_CurrentSchemaVersion" ON catalog."EquipmentTypes" ("Id", "CurrentSchemaVersion");

            {activeColDdl}

            CREATE TABLE catalog."EquipmentTypeCategories" (
                "EquipmentTypeId" uuid NOT NULL REFERENCES catalog."EquipmentTypes" ("Id") ON DELETE CASCADE,
                "CategoryId" uuid NOT NULL REFERENCES catalog."Categories" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "PK_EquipmentTypeCategories" PRIMARY KEY ("EquipmentTypeId", "CategoryId")
            );
            CREATE INDEX "IX_EquipmentTypeCategories_CategoryId" ON catalog."EquipmentTypeCategories" ("CategoryId");

            CREATE TABLE catalog."EquipmentTypeMedia" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "EquipmentTypeId" uuid NOT NULL REFERENCES catalog."EquipmentTypes" ("Id") ON DELETE CASCADE,
                "MediaId" uuid NOT NULL,
                "DisplayOrder" integer NOT NULL,
                "IsThumbnail" boolean NOT NULL
            );
            CREATE UNIQUE INDEX "IX_EquipmentTypeMedia_EquipmentTypeId_MediaId" ON catalog."EquipmentTypeMedia" ("EquipmentTypeId", "MediaId");
            CREATE UNIQUE INDEX "UX_EquipmentTypeMedia_EquipmentTypeId_Thumbnail" ON catalog."EquipmentTypeMedia" ("EquipmentTypeId") WHERE ("IsThumbnail" = TRUE);

            CREATE TABLE catalog."EquipmentTypeSpecSchemas" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "EquipmentTypeId" uuid NOT NULL REFERENCES catalog."EquipmentTypes" ("Id") ON DELETE CASCADE,
                "Version" integer NOT NULL,
                "Definition" jsonb NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "PublishedAt" timestamp with time zone,
                CONSTRAINT "AK_EquipmentTypeSpecSchemas_EquipmentTypeId_Version" UNIQUE ("EquipmentTypeId", "Version"),
                CONSTRAINT "CK_EquipmentTypeSpecSchemas_Version_Positive" CHECK ("Version" > 0)
            );
            CREATE UNIQUE INDEX "UX_EquipmentTypeSpecSchemas_EquipmentTypeId_Draft" ON catalog."EquipmentTypeSpecSchemas" ("EquipmentTypeId") WHERE ("PublishedAt" IS NULL);


            CREATE TABLE catalog."Brands" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Code" character varying(50) NOT NULL,
                "Name" character varying(200) NOT NULL,
                "Slug" character varying(200) NOT NULL,
                "LogoMediaId" uuid,
                "IsActive" boolean NOT NULL DEFAULT TRUE,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone
            );
            CREATE UNIQUE INDEX "IX_Brands_Code" ON catalog."Brands" ("Code");
            CREATE UNIQUE INDEX "IX_Brands_Slug" ON catalog."Brands" ("Slug");
            CREATE INDEX "IX_Brands_CreatedAt" ON catalog."Brands" ("CreatedAt");
            CREATE INDEX "IX_Brands_IsActive" ON catalog."Brands" ("IsActive");
            """;

        await using (var cmd = new NpgsqlCommand(ddl, conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // Seed dữ liệu mẫu
        await using var seedCmd = new NpgsqlCommand(
            """
            INSERT INTO catalog."Categories" ("Id", "Name", "Slug", "Description", "IsActive", "CreatedAtUtc")
            VALUES (@catId, 'Âm thanh', 'am-thanh', 'Thiết bị âm thanh', TRUE, @now);

            INSERT INTO catalog."Brands" ("Id", "Code", "Name", "Slug", "IsActive", "CreatedAt")
            VALUES (@brandId, 'SONY', 'Sony Corporation', 'sony', TRUE, @now);

            INSERT INTO catalog."EquipmentTypes" ("Id", "Code", "Name", "Description", "Status", "CurrentSchemaVersion", "CreatedAt", "PublishedAt")
            VALUES
            (@eq1Id, 'MIC-001', 'Micro không dây', 'Micro cầm tay sân khấu', 'Published', 1, @now - INTERVAL '2 days', @now - INTERVAL '1 day'),
            (@eq2Id, 'CAM-001', 'Máy quay phim', 'Máy quay 4K chuyên nghiệp', 'Draft', 1, @now - INTERVAL '1 day', NULL);

            INSERT INTO catalog."EquipmentTypeSpecSchemas" ("Id", "EquipmentTypeId", "Version", "Definition", "CreatedAt", "PublishedAt")
            VALUES
            (gen_random_uuid(), @eq1Id, 1, '{"fields":[{"key":"weight","label":"Trọng lượng","dataType":"number","isRequired":true}]}'::jsonb, @now - INTERVAL '2 days', @now - INTERVAL '1 day'),
            (gen_random_uuid(), @eq2Id, 1, '{"fields":[{"key":"resolution","label":"Độ phân giải","dataType":"text","isRequired":false}]}'::jsonb, @now - INTERVAL '1 day', @now - INTERVAL '1 day');

            INSERT INTO catalog."EquipmentTypeCategories" ("EquipmentTypeId", "CategoryId")
            VALUES (@eq1Id, @catId), (@eq2Id, @catId);

            INSERT INTO catalog."EquipmentTypeMedia" ("Id", "EquipmentTypeId", "MediaId", "DisplayOrder", "IsThumbnail")
            VALUES
            (@m1Id, @eq1Id, gen_random_uuid(), 0, TRUE),
            (@m2Id, @eq2Id, gen_random_uuid(), 0, TRUE);

            ALTER TABLE catalog."EquipmentTypes"
                ADD CONSTRAINT "FK_EquipmentTypes_EquipmentTypeSpecSchemas_CurrentSchema"
                FOREIGN KEY ("Id", "CurrentSchemaVersion")
                REFERENCES catalog."EquipmentTypeSpecSchemas" ("EquipmentTypeId", "Version")
                ON DELETE RESTRICT;
            """, conn);

        seedCmd.Parameters.AddWithValue("catId", categoryId);
        seedCmd.Parameters.AddWithValue("brandId", brandId);
        seedCmd.Parameters.AddWithValue("eq1Id", eq1Id);
        seedCmd.Parameters.AddWithValue("eq2Id", eq2Id);
        seedCmd.Parameters.AddWithValue("m1Id", mediaId1);
        seedCmd.Parameters.AddWithValue("m2Id", mediaId2);
        seedCmd.Parameters.AddWithValue("now", now);
        await seedCmd.ExecuteNonQueryAsync();
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
