using System.Diagnostics.CodeAnalysis;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
[Collection(PostgreSqlTestGroup.Name)]
public sealed class CategoryPostgreSqlIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly CatalogServiceDbContext _dbContext;
    private readonly CategoryRepository _repository;

    public CategoryPostgreSqlIntegrationTests()
    {
        var options = CreateContextOptions();
        _dbContext = new CatalogServiceDbContext(options);
        _repository = new CategoryRepository(_dbContext);
    }

    public async Task InitializeAsync()
    {
        await _dbContext.Database.MigrateAsync();
        await _dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE \"catalog\".\"Categories\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    private static DbContextOptions<CatalogServiceDbContext> CreateContextOptions() =>
        new DbContextOptionsBuilder<CatalogServiceDbContext>()
            .UseNpgsql(PostgreSqlFixture.ConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable(
                    CatalogServiceDbContext.MigrationsHistoryTableName,
                    CatalogServiceDbContext.SchemaName))
            .Options;

    [Fact]
    public async Task Search_WithPostgreSqlILike_MatchesNameAndSlugCaseInsensitively()
    {
        await _repository.AddAsync(new Category
        {
            Id = Guid.NewGuid(),
            Name = "Cinema Cameras",
            Slug = "cinema-cameras",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
        });

        await _repository.AddAsync(new Category
        {
            Id = Guid.NewGuid(),
            Name = "Audio Recording",
            Slug = "audio-rec",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        });

        await _repository.AddAsync(new Category
        {
            Id = Guid.NewGuid(),
            Name = "Drone Accessories",
            Slug = "quadcopter-drones",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        });

        // 1. Tìm theo Name khác hoa thường: "cinema" matches "Cinema Cameras"
        var res1 = await _repository.SearchAsync(new CategorySearchQuery { Keyword = "cinema" });
        Assert.Single(res1.Items);
        Assert.Equal("Cinema Cameras", res1.Items[0].Name);

        // 2. Tìm theo Slug khác hoa thường: "QUADCOPTER" matches "quadcopter-drones"
        var res2 = await _repository.SearchAsync(new CategorySearchQuery { Keyword = "QUADCOPTER" });
        Assert.Single(res2.Items);
        Assert.Equal("Drone Accessories", res2.Items[0].Name);

        // 3. Keyword có khoảng trắng thừa: trim keyword
        var res3 = await _repository.SearchAsync(new CategorySearchQuery { Keyword = "  recording  " });
        Assert.Single(res3.Items);
        Assert.Equal("Audio Recording", res3.Items[0].Name);

        // 4. Keyword rỗng: không tạo điều kiện lọc, trả về tất cả 3 items
        var resEmpty = await _repository.SearchAsync(new CategorySearchQuery { Keyword = "   " });
        Assert.Equal(3, resEmpty.TotalCount);
        Assert.Equal(3, resEmpty.Items.Count);
    }

    [Fact]
    public async Task Search_SortingAndPagination_WithStableIdTieBreaker()
    {
        var id1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var id2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var id3 = Guid.Parse("00000000-0000-0000-0000-000000000003");

        await _repository.AddAsync(new Category
        {
            Id = id1,
            Name = "Bravo",
            Slug = "bravo",
            CreatedAtUtc = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc)
        });

        await _repository.AddAsync(new Category
        {
            Id = id2,
            Name = "Alpha",
            Slug = "alpha",
            CreatedAtUtc = new DateTime(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc)
        });

        await _repository.AddAsync(new Category
        {
            Id = id3,
            Name = "Charlie",
            Slug = "charlie",
            CreatedAtUtc = new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc)
        });

        // 1. Sort name_asc
        var nameAsc = await _repository.SearchAsync(new CategorySearchQuery { Sort = "name_asc" });
        Assert.Equal(["Alpha", "Bravo", "Charlie"], nameAsc.Items.Select(i => i.Name));

        // 2. Sort name_desc
        var nameDesc = await _repository.SearchAsync(new CategorySearchQuery { Sort = "name_desc" });
        Assert.Equal(["Charlie", "Bravo", "Alpha"], nameDesc.Items.Select(i => i.Name));

        // 3. Sort newest (default)
        var newest = await _repository.SearchAsync(new CategorySearchQuery { Sort = "newest" });
        Assert.Equal(["Charlie", "Alpha", "Bravo"], newest.Items.Select(i => i.Name));

        // 4. Phân trang page 1, pageSize 2
        var page1 = await _repository.SearchAsync(new CategorySearchQuery { Sort = "name_asc", Page = 1, PageSize = 2 });
        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(["Alpha", "Bravo"], page1.Items.Select(i => i.Name));
        Assert.Equal(2, page1.TotalPages);

        // 5. Phân trang page 2, pageSize 2
        var page2 = await _repository.SearchAsync(new CategorySearchQuery { Sort = "name_asc", Page = 2, PageSize = 2 });
        Assert.Single(page2.Items);
        Assert.Equal("Charlie", page2.Items[0].Name);

        // 6. Trang vượt quá tổng số trang: trả về rỗng nhưng metadata đúng
        var pageBeyond = await _repository.SearchAsync(new CategorySearchQuery { Sort = "name_asc", Page = 10, PageSize = 2 });
        Assert.Empty(pageBeyond.Items);
        Assert.Equal(3, pageBeyond.TotalCount);
        Assert.Equal(10, pageBeyond.Page);
        Assert.Equal(2, pageBeyond.PageSize);
        Assert.Equal(2, pageBeyond.TotalPages);
    }

    [Fact]
    public async Task Search_TieBreaker_OrdersByIdWhenSortValuesAreEqual()
    {
        var idA = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var idB = Guid.Parse("00000000-0000-0000-0000-000000000002");
        DateTime sameTime = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

        await _repository.AddAsync(new Category
        {
            Id = idB,
            Name = "SameName",
            Slug = "same-b",
            CreatedAtUtc = sameTime
        });

        await _repository.AddAsync(new Category
        {
            Id = idA,
            Name = "SameName",
            Slug = "same-a",
            CreatedAtUtc = sameTime
        });

        // Cả hai có cùng Name và cùng CreatedAtUtc -> tie-breaker Id ASC
        var result = await _repository.SearchAsync(new CategorySearchQuery { Sort = "name_asc" });
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(idA, result.Items[0].Id);
        Assert.Equal(idB, result.Items[1].Id);
    }

    [Fact]
    public async Task Search_Admin_ActiveFiltering()
    {
        await _repository.AddAsync(new Category
        {
            Id = Guid.NewGuid(),
            Name = "Active 1",
            Slug = "active-1",
            IsActive = true
        });

        await _repository.AddAsync(new Category
        {
            Id = Guid.NewGuid(),
            Name = "Active 2",
            Slug = "active-2",
            IsActive = true
        });

        await _repository.AddAsync(new Category
        {
            Id = Guid.NewGuid(),
            Name = "Inactive 1",
            Slug = "inactive-1",
            IsActive = false
        });

        // 1. IsActive = null -> lấy cả 3
        var all = await _repository.SearchAsync(new CategorySearchQuery { IsActive = null });
        Assert.Equal(3, all.TotalCount);
        Assert.Equal(3, all.Items.Count);

        // 2. IsActive = true -> chỉ lấy 2 active
        var activeOnly = await _repository.SearchAsync(new CategorySearchQuery { IsActive = true });
        Assert.Equal(2, activeOnly.TotalCount);
        Assert.All(activeOnly.Items, c => Assert.True(c.IsActive));

        // 3. IsActive = false -> chỉ lấy 1 inactive
        var inactiveOnly = await _repository.SearchAsync(new CategorySearchQuery { IsActive = false });
        Assert.Equal(1, inactiveOnly.TotalCount);
        Assert.All(inactiveOnly.Items, c => Assert.False(c.IsActive));
    }

    [Fact]
    public async Task DisableAndEnable_Idempotency_PersistsInDatabase()
    {
        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = "Microphones",
            Slug = "microphones",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        await _repository.AddAsync(category);

        // Disable lần 1
        DateTime t1 = DateTime.UtcNow;
        bool dis1 = category.Disable(t1);
        Assert.True(dis1);
        await _repository.UpdateAsync(category);

        var loaded1 = await _repository.GetByIdAsync(category.Id);
        Assert.NotNull(loaded1);
        Assert.False(loaded1.IsActive);
        Assert.NotNull(loaded1.UpdatedAtUtc);
        DateTime disabledAt = loaded1.UpdatedAtUtc.Value;

        // Disable lần 2 (idempotent): Disable trả false, không gọi update DB
        bool dis2 = category.Disable(DateTime.UtcNow.AddMinutes(5));
        Assert.False(dis2);
        Assert.Equal(disabledAt, category.UpdatedAtUtc);

        // Enable lần 1
        DateTime t2 = DateTime.UtcNow.AddMinutes(10);
        bool en1 = category.Enable(t2);
        Assert.True(en1);
        await _repository.UpdateAsync(category);

        var loaded2 = await _repository.GetByIdAsync(category.Id);
        Assert.NotNull(loaded2);
        Assert.True(loaded2.IsActive);
        DateTime enabledAt = loaded2.UpdatedAtUtc!.Value;

        // Enable lần 2 (idempotent): Enable trả false, không gọi update DB
        bool en2 = category.Enable(DateTime.UtcNow.AddMinutes(15));
        Assert.False(en2);
        Assert.Equal(enabledAt, category.UpdatedAtUtc);
    }

    [Fact]
    public async Task EquipmentTypeRelationships_PreservedWhenCategoryDisabledAndEnabled()
    {
        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = "Lighting",
            Slug = "lighting",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        await _repository.AddAsync(category);

        var eq = new EquipmentType
        {
            Id = Guid.NewGuid(),
            Code = "LIGHT-001",
            Name = "Studio LED Light",
            CreatedAt = DateTime.UtcNow
        };
        eq.Categories.Add(category);
        _dbContext.EquipmentTypes.Add(eq);
        await _dbContext.SaveChangesAsync();

        // Xác nhận quan hệ ban đầu
        var eqLoaded = await _dbContext.EquipmentTypes
            .Include(e => e.Categories)
            .FirstOrDefaultAsync(e => e.Id == eq.Id);
        Assert.NotNull(eqLoaded);
        Assert.Single(eqLoaded.Categories);
        Assert.Equal(category.Id, eqLoaded.Categories.First().Id);

        // Disable category
        category.Disable(DateTime.UtcNow);
        await _repository.UpdateAsync(category);

        // Quan hệ equipment type vẫn còn nguyên
        var eqAfterDisable = await _dbContext.EquipmentTypes
            .Include(e => e.Categories)
            .FirstOrDefaultAsync(e => e.Id == eq.Id);
        Assert.NotNull(eqAfterDisable);
        Assert.Single(eqAfterDisable.Categories);
        Assert.Equal(category.Id, eqAfterDisable.Categories.First().Id);

        // Enable category
        category.Enable(DateTime.UtcNow);
        await _repository.UpdateAsync(category);

        // Quan hệ equipment type vẫn còn nguyên
        var eqAfterEnable = await _dbContext.EquipmentTypes
            .Include(e => e.Categories)
            .FirstOrDefaultAsync(e => e.Id == eq.Id);
        Assert.NotNull(eqAfterEnable);
        Assert.Single(eqAfterEnable.Categories);
        Assert.Equal(category.Id, eqAfterEnable.Categories.First().Id);
    }
}
