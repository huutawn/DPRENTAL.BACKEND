using System.Diagnostics.CodeAnalysis;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
[Collection(PostgreSqlTestGroup.Name)]
public sealed class PostgreSqlIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly CatalogServiceDbContext dbContext;
    private readonly EquipmentTypeRepository repository;

    public PostgreSqlIntegrationTests()
    {
        var options = CreateContextOptions();
        dbContext = new CatalogServiceDbContext(options);
        repository = new EquipmentTypeRepository(dbContext);
    }

    public async Task InitializeAsync()
    {
        await dbContext.Database.MigrateAsync();
        await dbContext.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"catalog\".\"EquipmentTypeCategories\", \"catalog\".\"EquipmentTypeMedia\", \"catalog\".\"EquipmentTypes\", \"catalog\".\"Categories\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        dbContext.Dispose();
    }

    private static DbContextOptions<CatalogServiceDbContext> CreateContextOptions() =>
        new DbContextOptionsBuilder<CatalogServiceDbContext>()
            .UseNpgsql(PostgreSqlFixture.ConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable(
                    CatalogServiceDbContext.MigrationsHistoryTableName,
                    CatalogServiceDbContext.SchemaName))
            .Options;

    [Fact]
    public async Task Search_FiltersAndCalculatesTotalCountBeforePaging()
    {
        Category catA = await CreateCategoryAsync("Cat A");
        Category catB = await CreateCategoryAsync("Cat B");

        for (int i = 1; i <= 5; i++)
        {
            await CreateEquipmentTypeAsync(
                $"EQ-A-{i}",
                $"Equip A {i}",
                catA.Id,
                EquipmentTypeStatus.Published,
                description: $"Mô tả loại {i}");
        }

        await CreateEquipmentTypeAsync("EQ-B-1", "Equip B 1", catB.Id, EquipmentTypeStatus.Published);

        var request = new GetEquipmentTypesRequest
        {
            CategoryId = catA.Id,
            Page = 1,
            PageSize = 2
        };

        PagedResult<EquipmentTypeListItemDto> result = await repository.SearchPublishedAsync(request);

        Assert.Equal(5, result.TotalCount);
        Assert.Equal(3, result.TotalPages);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item =>
        {
            Assert.Contains(item.Categories, c => c.Id == catA.Id);
        });
    }

    [Fact]
    public async Task Search_ManyToMany_MultipleCategories_NoDuplicatesAndBidirectional()
    {
        Category cat1 = await CreateCategoryAsync("Cat Multi 1");
        Category cat2 = await CreateCategoryAsync("Cat Multi 2");
        Category cat3 = await CreateCategoryAsync("Cat Multi 3");

        var eq1 = new EquipmentType
        {
            Id = Guid.NewGuid(),
            Code = "MULTI-001",
            Name = "Multi 1",
            Categories = [cat1, cat2, cat3],
            CreatedAt = DateTime.UtcNow,
        };
        var eq2 = new EquipmentType
        {
            Id = Guid.NewGuid(),
            Code = "MULTI-002",
            Name = "Multi 2",
            Categories = [cat1, cat2],
            CreatedAt = DateTime.UtcNow,
        };
        dbContext.EquipmentTypes.AddRange(eq1, eq2);
        await dbContext.SaveChangesAsync();

        var schema1 = EquipmentTypeSpecSchema.CreatePublished(
            Guid.NewGuid(), eq1.Id, 1, new EquipmentTypeSpecDefinition { Fields = [] }, eq1.CreatedAt, eq1.CreatedAt);
        var schema2 = EquipmentTypeSpecSchema.CreatePublished(
            Guid.NewGuid(), eq2.Id, 1, new EquipmentTypeSpecDefinition { Fields = [] }, eq2.CreatedAt, eq2.CreatedAt);
        dbContext.EquipmentTypeSpecSchemas.AddRange(schema1, schema2);
        eq1.ApplyPublishedSchema(schema1, eq1.CreatedAt);
        eq2.ApplyPublishedSchema(schema2, eq2.CreatedAt);
        await dbContext.SaveChangesAsync();

        eq1.Publish(DateTime.UtcNow);
        eq2.Publish(DateTime.UtcNow);
        await dbContext.SaveChangesAsync();
        // 1. Search Category 1 -> Phải trả về đúng 2 equipment, không trùng lặp row
        var result = await repository.SearchPublishedAsync(new GetEquipmentTypesRequest { CategoryId = cat1.Id, Page = 1, PageSize = 10 });
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.Items.Select(i => i.Id).Distinct().Count());

        // 2. Kiểm tra quan hệ 2 chiều:
        var loadedCat = await dbContext.Categories.Include(c => c.EquipmentTypes).FirstAsync(c => c.Id == cat1.Id);
        Assert.Contains(loadedCat.EquipmentTypes, e => e.Id == eq1.Id);
        Assert.Contains(loadedCat.EquipmentTypes, e => e.Id == eq2.Id);

        var loadedEq = await dbContext.EquipmentTypes.Include(e => e.Categories).FirstAsync(e => e.Id == eq1.Id);
        Assert.Equal(3, loadedEq.Categories.Count);
    }

    [Fact]
    public async Task Search_ILikeKeyword_MatchesCaseInsensitive_Code_Name_AndDescription()
    {
        Category cat = await CreateCategoryAsync("Cameras");

        await CreateEquipmentTypeAsync("CAM-SONY-001", "SONY Alpha A7 IV", cat.Id, EquipmentTypeStatus.Published, description: "Máy ảnh mirrorless fullframe");
        await CreateEquipmentTypeAsync("CAM-CANON-002", "Canon EOS R5", cat.Id, EquipmentTypeStatus.Published, description: "Quay phim 8K chuyên nghiệp");
        await CreateEquipmentTypeAsync("CAM-BMPCC-003", "Blackmagic Pocket 6K", cat.Id, EquipmentTypeStatus.Published, description: "EF mount cinema camera");

        var resCode = await repository.SearchPublishedAsync(new GetEquipmentTypesRequest { Keyword = "sony-001" });
        Assert.Single(resCode.Items);
        Assert.Equal("CAM-SONY-001", resCode.Items[0].Code);

        var resName = await repository.SearchPublishedAsync(new GetEquipmentTypesRequest { Keyword = "alpha" });
        Assert.Single(resName.Items);
        Assert.Equal("SONY Alpha A7 IV", resName.Items[0].Name);

        var resDesc = await repository.SearchPublishedAsync(new GetEquipmentTypesRequest { Keyword = "cinema" });
        Assert.Single(resDesc.Items);
        Assert.Equal("Blackmagic Pocket 6K", resDesc.Items[0].Name);
    }

    [Fact]
    public async Task Search_SortsStably_ByNameAsc_NameDesc_AndDefault()
    {
        Category cat = await CreateCategoryAsync("Lenses");

        var eq1 = await CreateEquipmentTypeAsync("LENS-001", "Lens A", cat.Id, EquipmentTypeStatus.Published);
        var eq2 = await CreateEquipmentTypeAsync("LENS-002", "Lens B", cat.Id, EquipmentTypeStatus.Published);
        var eq3 = await CreateEquipmentTypeAsync("LENS-003", "Lens C", cat.Id, EquipmentTypeStatus.Published);

        var ascResult = await repository.SearchPublishedAsync(new GetEquipmentTypesRequest { Sort = "name_asc" });
        Assert.Equal(eq1.Id, ascResult.Items[0].Id);
        Assert.Equal(eq2.Id, ascResult.Items[1].Id);
        Assert.Equal(eq3.Id, ascResult.Items[2].Id);

        var descResult = await repository.SearchPublishedAsync(new GetEquipmentTypesRequest { Sort = "name_desc" });
        Assert.Equal(eq3.Id, descResult.Items[0].Id);
        Assert.Equal(eq2.Id, descResult.Items[1].Id);
        Assert.Equal(eq1.Id, descResult.Items[2].Id);

        var defaultResult = await repository.SearchPublishedAsync(new GetEquipmentTypesRequest());
        Assert.Equal(eq3.Id, defaultResult.Items[0].Id);
    }

    [Fact]
    public async Task Sorting_WithTieBreaker_OrdersByIdStably()
    {
        Category cat = await CreateCategoryAsync("TieBreakers");

        Guid id1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid id2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
        DateTime sharedTime = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        var item1 = new EquipmentType
        {
            Id = id1,
            Code = "TIE-001",
            Name = "Identical Name",
            Categories = [cat],
            CreatedAt = sharedTime
        };
        var item2 = new EquipmentType
        {
            Id = id2,
            Code = "TIE-002",
            Name = "Identical Name",
            Categories = [cat],
            CreatedAt = sharedTime
        };
        dbContext.EquipmentTypes.AddRange(item1, item2);
        await dbContext.SaveChangesAsync();

        var s1 = EquipmentTypeSpecSchema.CreatePublished(
            Guid.NewGuid(), id1, 1, new EquipmentTypeSpecDefinition { Fields = [] }, sharedTime, sharedTime);
        var s2 = EquipmentTypeSpecSchema.CreatePublished(
            Guid.NewGuid(), id2, 1, new EquipmentTypeSpecDefinition { Fields = [] }, sharedTime, sharedTime);
        dbContext.EquipmentTypeSpecSchemas.AddRange(s1, s2);
        item1.ApplyPublishedSchema(s1, sharedTime);
        item2.ApplyPublishedSchema(s2, sharedTime);
        await dbContext.SaveChangesAsync();

        item1.Publish(sharedTime);
        item2.Publish(sharedTime);
        await dbContext.SaveChangesAsync();

        // Sort theo name_asc: Name giống nhau -> tie-breaker Id tăng dần
        var ascRes = await repository.SearchPublishedAsync(new GetEquipmentTypesRequest { Sort = "name_asc" });
        Assert.Equal(2, ascRes.Items.Count);
        Assert.Equal(id1, ascRes.Items[0].Id);
        Assert.Equal(id2, ascRes.Items[1].Id);

        // Sort mặc định (CreatedAt giảm dần): CreatedAt giống nhau -> tie-breaker Id tăng dần
        var defRes = await repository.SearchPublishedAsync(new GetEquipmentTypesRequest());
        Assert.Equal(2, defRes.Items.Count);
        Assert.Equal(id1, defRes.Items[0].Id);
        Assert.Equal(id2, defRes.Items[1].Id);
    }

    [Fact]
    public async Task SearchAndDetail_PicksThumbnailMedia_AndOrdersDetailMedia()
    {
        Category cat = await CreateCategoryAsync("Audio");

        EquipmentType eq = await CreateEquipmentTypeAsync("MIC-001", "Mic Wireless", cat.Id, EquipmentTypeStatus.Published);

        Guid mediaNormal = Guid.NewGuid();
        Guid mediaThumb = Guid.NewGuid();

        dbContext.EquipmentTypeMedia.AddRange(
            new EquipmentTypeMedia { Id = Guid.NewGuid(), EquipmentTypeId = eq.Id, MediaId = mediaNormal, IsThumbnail = false, DisplayOrder = 0 },
            new EquipmentTypeMedia { Id = Guid.NewGuid(), EquipmentTypeId = eq.Id, MediaId = mediaThumb, IsThumbnail = true, DisplayOrder = 1 }
        );
        await dbContext.SaveChangesAsync();

        var searchResult = await repository.SearchPublishedAsync(new GetEquipmentTypesRequest { Keyword = "Wireless" });
        Assert.Single(searchResult.Items);
        Assert.Equal(mediaThumb, searchResult.Items[0].ThumbnailMediaId);

        EquipmentTypeDetailDto? detail = await repository.GetPublishedByIdAsync(eq.Id);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.Media.Count);
        Assert.Equal(mediaNormal, detail.Media[0].MediaId);
        Assert.Equal(mediaThumb, detail.Media[1].MediaId);
    }

    [Fact]
    public async Task ThumbnailSelection_Fallback_WhenNoThumbnailExplicitlyMarked()
    {
        Category cat = await CreateCategoryAsync("Guitars Fallback");
        EquipmentType eq = await CreateEquipmentTypeAsync("GUI-FALLBACK", "Acoustic", cat.Id, EquipmentTypeStatus.Published);

        Guid mediaLowOrder = Guid.NewGuid();
        Guid mediaHighOrder = Guid.NewGuid();

        // Cả 2 media đều IsThumbnail = false: fallback chọn DisplayOrder nhỏ nhất
        dbContext.EquipmentTypeMedia.AddRange(
            new EquipmentTypeMedia { Id = Guid.NewGuid(), EquipmentTypeId = eq.Id, MediaId = mediaHighOrder, IsThumbnail = false, DisplayOrder = 5 },
            new EquipmentTypeMedia { Id = Guid.NewGuid(), EquipmentTypeId = eq.Id, MediaId = mediaLowOrder, IsThumbnail = false, DisplayOrder = 1 }
        );
        await dbContext.SaveChangesAsync();

        var res = await repository.SearchPublishedAsync(new GetEquipmentTypesRequest { Keyword = "Acoustic" });
        Assert.Single(res.Items);
        Assert.Equal(mediaLowOrder, res.Items[0].ThumbnailMediaId);
    }

    [Fact]
    public async Task SearchAndDetail_NeverExposesDraftOrDisabledEquipmentTypes()
    {
        Category cat = await CreateCategoryAsync("Drones");

        EquipmentType draft = await CreateEquipmentTypeAsync("DRONE-001", "Drone Draft", cat.Id, EquipmentTypeStatus.Draft);
        EquipmentType disabled = await CreateEquipmentTypeAsync("DRONE-002", "Drone Disabled", cat.Id, EquipmentTypeStatus.Disabled);
        EquipmentType published = await CreateEquipmentTypeAsync("DRONE-003", "Drone Pub", cat.Id, EquipmentTypeStatus.Published);

        var result = await repository.SearchPublishedAsync(new GetEquipmentTypesRequest { Keyword = "Drone" });
        Assert.Single(result.Items);
        Assert.Equal(published.Id, result.Items[0].Id);

        EquipmentTypeDetailDto? detailDraft = await repository.GetPublishedByIdAsync(draft.Id);
        Assert.Null(detailDraft);

        EquipmentTypeDetailDto? detailDisabled = await repository.GetPublishedByIdAsync(disabled.Id);
        Assert.Null(detailDisabled);

        EquipmentTypeDetailDto? detailPub = await repository.GetPublishedByIdAsync(published.Id);
        Assert.NotNull(detailPub);
        Assert.Equal(published.Id, detailPub.Id);
    }

    [Fact]
    public async Task ConcurrentCreate_SameCode_OneSucceeds_OneReturns409Conflict()
    {
        Category cat = await CreateCategoryAsync("Race Create");

        using var ctx1 = new CatalogServiceDbContext(CreateContextOptions());
        using var ctx2 = new CatalogServiceDbContext(CreateContextOptions());

        var repo1 = new EquipmentTypeRepository(ctx1);
        var repo2 = new EquipmentTypeRepository(ctx2);

        using var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            var cat1 = await ctx1.Categories.FindAsync(cat.Id);
            var eq = new EquipmentType
            {
                Id = Guid.NewGuid(),
                Categories = [cat1!],
                Code = "RACE-CREATE-001",
                Name = "Race 1",
                CreatedAt = DateTime.UtcNow
            };
            return await repo1.AddAsync(eq, null);
        });

        var task2 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            var cat2 = await ctx2.Categories.FindAsync(cat.Id);
            var eq = new EquipmentType
            {
                Id = Guid.NewGuid(),
                Categories = [cat2!],
                Code = "RACE-CREATE-001",
                Name = "Race 2",
                CreatedAt = DateTime.UtcNow
            };
            return await repo2.AddAsync(eq, null);
        });

        var results = await Task.WhenAll(task1, task2);

        Assert.Single(results, r => r.IsSuccess);
        Assert.Single(results, r => r.IsFailure && r.Error.Code == CatalogErrorCode.EquipmentType.DuplicateCode);
        // Kiểm tra database chỉ có đúng 1 row
        int count = await dbContext.EquipmentTypes.CountAsync(e => e.Code == "RACE-CREATE-001");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ConcurrentPatch_SameCode_OneSucceeds_OneReturns409Conflict()
    {
        Category cat = await CreateCategoryAsync("Race Patch");
        var eq1 = await CreateEquipmentTypeAsync("P-CODE-1", "Name 1", cat.Id, EquipmentTypeStatus.Published);
        var eq2 = await CreateEquipmentTypeAsync("P-CODE-2", "Name 2", cat.Id, EquipmentTypeStatus.Published);

        using var ctx1 = new CatalogServiceDbContext(CreateContextOptions());
        using var ctx2 = new CatalogServiceDbContext(CreateContextOptions());

        var repo1 = new EquipmentTypeRepository(ctx1);
        var repo2 = new EquipmentTypeRepository(ctx2);

        var entity1 = await repo1.GetByIdAsync(eq1.Id);
        var entity2 = await repo2.GetByIdAsync(eq2.Id);
        Assert.NotNull(entity1);
        Assert.NotNull(entity2);

        const string targetCode = "TARGET-RACE-CODE";
        entity1.Code = targetCode;
        entity2.Code = targetCode;

        using var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await repo1.SaveAsync(entity1, null);
        });

        var task2 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await repo2.SaveAsync(entity2, null);
        });

        var results = await Task.WhenAll(task1, task2);

        Assert.Single(results, r => r.IsSuccess);
        Assert.Single(results, r => r.IsFailure && r.Error.Code == CatalogErrorCode.EquipmentType.DuplicateCode);

        int count = await dbContext.EquipmentTypes.CountAsync(e => e.Code == targetCode);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ConcurrentMediaPatch_SameEntity_OneSucceeds_OneReturnsConcurrencyConflict()
    {
        Category cat = await CreateCategoryAsync("Race Media");
        var eq = await CreateEquipmentTypeAsync("RACE-MEDIA-001", "Media Entity", cat.Id, EquipmentTypeStatus.Published);

        using var ctx1 = new CatalogServiceDbContext(CreateContextOptions());
        using var ctx2 = new CatalogServiceDbContext(CreateContextOptions());

        var repo1 = new EquipmentTypeRepository(ctx1);
        var repo2 = new EquipmentTypeRepository(ctx2);

        // Cả 2 context cùng đọc ban đầu -> cùng giữ original xmin
        var entity1 = await repo1.GetByIdAsync(eq.Id);
        var entity2 = await repo2.GetByIdAsync(eq.Id);
        Assert.NotNull(entity1);
        Assert.NotNull(entity2);

        Guid m1 = Guid.NewGuid();
        Guid m2 = Guid.NewGuid();

        using var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await repo1.SaveAsync(entity1, [new EquipmentTypeMediaItemDto(m1, 0, true)]);
        });

        var task2 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await repo2.SaveAsync(entity2, [new EquipmentTypeMediaItemDto(m2, 0, true)]);
        });

        var results = await Task.WhenAll(task1, task2);

        Assert.Single(results, r => r.IsSuccess);
        Assert.Single(results, r => r.IsFailure && r.Error.Code == CatalogErrorCode.EquipmentType.ConcurrencyConflict);

        // Kiểm tra database trong context mới
        using var verifyCtx = new CatalogServiceDbContext(CreateContextOptions());
        var mediaInDb = await verifyCtx.EquipmentTypeMedia.Where(m => m.EquipmentTypeId == eq.Id).ToListAsync();
        Assert.Single(mediaInDb);
    }

    [Fact]
    public async Task ConcurrentLifecycle_Publish_ReturnsDeterministicResult()
    {
        Category cat = await CreateCategoryAsync("Race Lifecycle");
        var eq = await CreateEquipmentTypeAsync("RACE-LIFE-001", "Draft Eq", cat.Id, EquipmentTypeStatus.Draft);
        var raceSchema = EquipmentTypeSpecSchema.CreatePublished(
            Guid.NewGuid(), eq.Id, 1, new EquipmentTypeSpecDefinition { Fields = [] }, eq.CreatedAt, eq.CreatedAt);
        dbContext.EquipmentTypeSpecSchemas.Add(raceSchema);
        eq.ApplyPublishedSchema(raceSchema, eq.CreatedAt);
        await dbContext.SaveChangesAsync();
        using var ctx1 = new CatalogServiceDbContext(CreateContextOptions());
        using var ctx2 = new CatalogServiceDbContext(CreateContextOptions());

        var repo1 = new EquipmentTypeRepository(ctx1);
        var repo2 = new EquipmentTypeRepository(ctx2);

        var entity1 = await repo1.GetByIdAsync(eq.Id);
        var entity2 = await repo2.GetByIdAsync(eq.Id);
        Assert.NotNull(entity1);
        Assert.NotNull(entity2);

        entity1.Publish(DateTime.UtcNow);
        entity2.Publish(DateTime.UtcNow);

        using var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await repo1.SaveStateTransitionAsync(entity1);
        });

        var task2 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await repo2.SaveStateTransitionAsync(entity2);
        });

        var results = await Task.WhenAll(task1, task2);

        Assert.Single(results, r => r.IsSuccess);
        Assert.Single(results, r => r.IsFailure && r.Error.Code == CatalogErrorCode.EquipmentType.ConcurrencyConflict);

        using var verifyCtx = new CatalogServiceDbContext(CreateContextOptions());
        var finalEntity = await verifyCtx.EquipmentTypes.FirstAsync(e => e.Id == eq.Id);
        Assert.Equal(EquipmentTypeStatus.Published, finalEntity.Status);
    }

    [Fact]
    public async Task Rollback_WhenTransactionFailsAfterParentOrThumbnail_RestoresAggregate()
    {
        Category cat = await CreateCategoryAsync("Rollback Category");
        var eq = await CreateEquipmentTypeAsync("ROLLBACK-001", "Original Name", cat.Id, EquipmentTypeStatus.Published, description: "Original Desc");

        Guid originalThumb = Guid.NewGuid();
        dbContext.EquipmentTypeMedia.Add(new EquipmentTypeMedia
        {
            Id = Guid.NewGuid(),
            EquipmentTypeId = eq.Id,
            MediaId = originalThumb,
            DisplayOrder = 0,
            IsThumbnail = true
        });
        await dbContext.SaveChangesAsync();

        DateTime originalUpdatedAt = eq.UpdatedAt ?? eq.CreatedAt;

        // Bắt đầu một transaction: thay đổi parent Name, đổi thumbnail, rồi fail bằng duplicate code
        await using (var txContext = new CatalogServiceDbContext(CreateContextOptions()))
        {
            var txRepo = new EquipmentTypeRepository(txContext);
            var entityToMutate = await txRepo.GetByIdAsync(eq.Id);
            Assert.NotNull(entityToMutate);

            entityToMutate.Name = "Changed Name in Failed Tx";
            // Gây lỗi bằng cách đổi code thành một code đã tồn tại
            var existingEq = await CreateEquipmentTypeAsync("CONFLICT-CODE", "Other", cat.Id, EquipmentTypeStatus.Draft);
            entityToMutate.Code = "CONFLICT-CODE";

            var desiredMedia = new List<EquipmentTypeMediaItemDto?>
            {
                new(Guid.NewGuid(), 0, true)
            };

            var saveResult = await txRepo.SaveAsync(entityToMutate, desiredMedia);
            Assert.True(saveResult.IsFailure);
            Assert.Equal(CatalogErrorCode.EquipmentType.DuplicateCode, saveResult.Error.Code);
        }

        // Kiểm tra trong context mới hoàn toàn: aggregate và media ban đầu không bị biến dạng
        using var checkContext = new CatalogServiceDbContext(CreateContextOptions());
        var restored = await checkContext.EquipmentTypes.Include(e => e.Media).FirstAsync(e => e.Id == eq.Id);

        Assert.Equal("ROLLBACK-001", restored.Code);
        Assert.Equal("Original Name", restored.Name);
        Assert.Equal("Original Desc", restored.Description);

        Assert.Single(restored.Media);
        Assert.Equal(originalThumb, restored.Media.First().MediaId);
        Assert.True(restored.Media.First().IsThumbnail);
    }

    [Fact]
    public async Task Rollback_WhenWriteFails_LeavesNoOrphanMediaOrEntity()
    {
        Category cat = await CreateCategoryAsync("Audio Wireless");

        await CreateEquipmentTypeAsync("EXISTING-CODE", "Existing", cat.Id, EquipmentTypeStatus.Published);

        var duplicateEq = new EquipmentType
        {
            Id = Guid.NewGuid(),
            Categories = [cat],
            Code = "EXISTING-CODE",
            Name = "Duplicate",
            CreatedAt = DateTime.UtcNow
        };

        Guid mediaId = Guid.NewGuid();
        var media = new List<EquipmentTypeMediaItemDto>
        {
            new(mediaId, 0, true)
        };

        var addResult = await repository.AddAsync(duplicateEq, media);
        Assert.True(addResult.IsFailure);
        Assert.Equal(CatalogErrorCode.EquipmentType.DuplicateCode, addResult.Error.Code);

        // Kiểm tra không có orphan media được lưu
        bool mediaExists = await dbContext.EquipmentTypeMedia.AnyAsync(m => m.MediaId == mediaId);
        Assert.False(mediaExists);
    }

    private async Task<Category> CreateCategoryAsync(string name)
    {
        Category cat = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTime.UtcNow
        };
        dbContext.Categories.Add(cat);
        await dbContext.SaveChangesAsync();
        return cat;
    }

    private async Task<EquipmentType> CreateEquipmentTypeAsync(
        string code,
        string name,
        Guid categoryId,
        EquipmentTypeStatus status,
        string? description = null)
    {
        var category = await dbContext.Categories.FindAsync(categoryId);
        EquipmentType eq = new()
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = name,
            Description = description,
            CreatedAt = DateTime.UtcNow,
            Categories = category != null ? [category] : []
        };
        dbContext.EquipmentTypes.Add(eq);
        await dbContext.SaveChangesAsync();

        if (status != EquipmentTypeStatus.Draft)
        {
            var schema = EquipmentTypeSpecSchema.CreatePublished(
                Guid.NewGuid(), eq.Id, 1, new EquipmentTypeSpecDefinition { Fields = [] }, eq.CreatedAt, eq.CreatedAt);
            dbContext.EquipmentTypeSpecSchemas.Add(schema);
            eq.ApplyPublishedSchema(schema, eq.CreatedAt);
            await dbContext.SaveChangesAsync();

            DateTime now = DateTime.UtcNow;
            if (status == EquipmentTypeStatus.Published)
            {
                eq.Publish(now);
            }
            else if (status == EquipmentTypeStatus.Disabled)
            {
                eq.Publish(now);
                eq.Disable(now.AddSeconds(1));
            }
            await dbContext.SaveChangesAsync();
        }

        return eq;
    }
}
