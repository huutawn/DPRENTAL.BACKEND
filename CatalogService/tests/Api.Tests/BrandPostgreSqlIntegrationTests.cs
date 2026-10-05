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
public sealed class BrandPostgreSqlIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly CatalogServiceDbContext dbContext;
    private readonly BrandRepository repository;

    public BrandPostgreSqlIntegrationTests()
    {
        var options = CreateContextOptions();
        dbContext = new CatalogServiceDbContext(options);
        repository = new BrandRepository(dbContext);
    }

    public async Task InitializeAsync()
    {
        await dbContext.Database.MigrateAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE \"catalog\".\"Brands\" CASCADE;");
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
    public async Task CreateBrand_PersistsInDatabase_WithUtcTimestamps_AndActiveByDefault()
    {
        var brand = new Brand
        {
            Id = Guid.NewGuid(),
            Code = "SONY",
            Name = "Sony Corporation",
            Slug = "sony",
            LogoMediaId = Guid.NewGuid(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = null
        };

        var addResult = await repository.AddAsync(brand);
        Assert.True(addResult.IsSuccess);

        var loaded = await repository.GetByIdAsync(brand.Id);
        Assert.NotNull(loaded);
        Assert.Equal(brand.Id, loaded.Id);
        Assert.Equal("SONY", loaded.Code);
        Assert.Equal("Sony Corporation", loaded.Name);
        Assert.Equal("sony", loaded.Slug);
        Assert.Equal(brand.LogoMediaId, loaded.LogoMediaId);
        Assert.True(loaded.IsActive);
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAt.Kind);
        Assert.Null(loaded.UpdatedAt);
    }

    [Fact]
    public async Task UniqueCode_EnforcedAcrossActiveAndInactiveBrands()
    {
        // 1. Tạo Brand 1 inactive với mã SONY
        var brand1 = new Brand
        {
            Id = Guid.NewGuid(),
            Code = "SONY",
            Name = "Sony Inactive",
            Slug = "sony-inactive",
            IsActive = false,
            CreatedAt = DateTime.UtcNow
        };
        var r1 = await repository.AddAsync(brand1);
        Assert.True(r1.IsSuccess);

        // 2. Cố gắng tạo Brand 2 với cùng mã SONY
        var brand2 = new Brand
        {
            Id = Guid.NewGuid(),
            Code = "SONY",
            Name = "Sony Active Duplicate",
            Slug = "sony-active-dup",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var r2 = await repository.AddAsync(brand2);

        Assert.True(r2.IsFailure);
        Assert.Equal(CatalogErrorCode.Brand.DuplicateCode, r2.Error.Code);
    }

    [Fact]
    public async Task UniqueSlug_EnforcedAcrossActiveAndInactiveBrands()
    {
        // 1. Tạo Brand 1 inactive với slug sony
        var brand1 = new Brand
        {
            Id = Guid.NewGuid(),
            Code = "SONY-1",
            Name = "Sony Inactive",
            Slug = "sony",
            IsActive = false,
            CreatedAt = DateTime.UtcNow
        };
        var r1 = await repository.AddAsync(brand1);
        Assert.True(r1.IsSuccess);

        // 2. Cố gắng tạo Brand 2 với cùng slug sony
        var brand2 = new Brand
        {
            Id = Guid.NewGuid(),
            Code = "SONY-2",
            Name = "Sony Active Duplicate",
            Slug = "sony",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var r2 = await repository.AddAsync(brand2);

        Assert.True(r2.IsFailure);
        Assert.Equal(CatalogErrorCode.Brand.DuplicateSlug, r2.Error.Code);
    }

    [Fact]
    public async Task ConcurrencyControl_XminPreventsLostUpdates()
    {
        // 1. Tạo brand ban đầu
        var brand = new Brand
        {
            Id = Guid.NewGuid(),
            Code = "CANON",
            Name = "Canon Inc.",
            Slug = "canon",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        await repository.AddAsync(brand);

        // 2. Khởi tạo 2 DbContext riêng biệt giả lập 2 request đồng thời
        using var contextA = new CatalogServiceDbContext(CreateContextOptions());
        using var contextB = new CatalogServiceDbContext(CreateContextOptions());

        var repoA = new BrandRepository(contextA);
        var repoB = new BrandRepository(contextB);

        var brandA = await repoA.GetByIdAsync(brand.Id);
        var brandB = await repoB.GetByIdAsync(brand.Id);
        Assert.NotNull(brandA);
        Assert.NotNull(brandB);

        // 3. Request A cập nhật thành công
        brandA.Name = "Canon Updated by A";
        brandA.Touch(DateTime.UtcNow);
        var resultA = await repoA.UpdateAsync(brandA);
        Assert.True(resultA.IsSuccess);

        // 4. Request B cập nhật trên dữ liệu cũ bị Conflict do xmin token thay đổi
        brandB.Name = "Canon Updated by B";
        brandB.Touch(DateTime.UtcNow);
        var resultB = await repoB.UpdateAsync(brandB);

        Assert.True(resultB.IsFailure);
        Assert.Equal(CatalogErrorCode.Brand.ConcurrencyConflict, resultB.Error.Code);
    }

    [Fact]
    public async Task DisableAndEnable_IdempotentBehavior_PreservesTimestamps()
    {
        var brand = new Brand
        {
            Id = Guid.NewGuid(),
            Code = "NIKON",
            Name = "Nikon Corporation",
            Slug = "nikon",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        await repository.AddAsync(brand);

        DateTime t1 = DateTime.UtcNow;
        brand.Disable(t1);
        var update1 = await repository.UpdateAsync(brand);
        Assert.True(update1.IsSuccess);
        Assert.False(update1.Value.IsActive);
        Assert.NotNull(update1.Value.UpdatedAt);
        DateTime disabledAt = update1.Value.UpdatedAt.Value;

        // Gọi Disable lần 2: Disable() trả về false nên không gọi UpdateAsync
        bool changedAgain = brand.Disable(DateTime.UtcNow.AddHours(1));
        Assert.False(changedAgain);
        Assert.Equal(disabledAt, brand.UpdatedAt);

        // Enable lần 1
        DateTime t2 = DateTime.UtcNow.AddHours(2);
        brand.Enable(t2);
        var update2 = await repository.UpdateAsync(brand);
        Assert.True(update2.IsSuccess);
        Assert.True(update2.Value.IsActive);
        DateTime enabledAt = update2.Value.UpdatedAt!.Value;

        // Gọi Enable lần 2: Enable() trả về false
        bool enabledAgain = brand.Enable(DateTime.UtcNow.AddHours(3));
        Assert.False(enabledAgain);
        Assert.Equal(enabledAt, brand.UpdatedAt);
    }

    [Fact]
    public async Task Search_WithPostgreSqlILike_SupportsCodeAndName()
    {
        await repository.AddAsync(new Brand
        {
            Id = Guid.NewGuid(),
            Code = "SONY-ALPHA",
            Name = "Sony Alpha Mirrorless",
            Slug = "sony-alpha",
            IsActive = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10)
        });

        await repository.AddAsync(new Brand
        {
            Id = Guid.NewGuid(),
            Code = "CANON-EOS",
            Name = "Canon EOS Cinema",
            Slug = "canon-eos",
            IsActive = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        });

        await repository.AddAsync(new Brand
        {
            Id = Guid.NewGuid(),
            Code = "BLACKMAGIC",
            Name = "Blackmagic Design",
            Slug = "blackmagic",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        // Tìm theo code case-insensitive "sony"
        var res1 = await repository.SearchAsync(new BrandSearchQuery { Keyword = "sony" });
        Assert.Single(res1.Items);
        Assert.Equal("SONY-ALPHA", res1.Items[0].Code);

        // Tìm theo name case-insensitive "cinema"
        var res2 = await repository.SearchAsync(new BrandSearchQuery { Keyword = "cinema" });
        Assert.Single(res2.Items);
        Assert.Equal("CANON-EOS", res2.Items[0].Code);

        // Tìm từ khóa chung "magic"
        var res3 = await repository.SearchAsync(new BrandSearchQuery { Keyword = "magic" });
        Assert.Single(res3.Items);
        Assert.Equal("BLACKMAGIC", res3.Items[0].Code);
    }

    [Fact]
    public async Task Search_NormalizesNameKeyword_MatchingStoredNfcAndCollapsedWhitespace()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new Application.Options.CatalogDiscoveryOptions { MaxKeywordLength = 100 });
        var service = new Application.Services.BrandService(
            repository,
            new DummyMediaClient(),
            new Application.Validators.BrandListQueryValidator(options),
            new Application.Validators.CreateBrandRequestValidator(),
            new Application.Validators.UpdateBrandRequestValidator(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Application.Services.BrandService>.Instance);

        await repository.AddAsync(new Brand
        {
            Id = Guid.NewGuid(),
            Code = "SONY-CORP",
            Name = "Sony Corporation",
            Slug = "sony-corp",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        // "Müller" stored in NFC
        await repository.AddAsync(new Brand
        {
            Id = Guid.NewGuid(),
            Code = "MUELLER",
            Name = "Müller German Optics",
            Slug = "mueller-optics",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        // Code with internal spaces
        await repository.AddAsync(new Brand
        {
            Id = Guid.NewGuid(),
            Code = "SPECIAL   SPACES",
            Name = "Special Spaces Brand",
            Slug = "special-spaces",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        // 1. Search with multiple spaces: "Sony   Corporation" matches "Sony Corporation"
        var resSpaces = await service.SearchPublicAsync(new Application.DTOs.Brands.GetBrandsRequest { Keyword = "Sony   Corporation" });
        Assert.True(resSpaces.IsSuccess);
        Assert.Single(resSpaces.Value.Items);
        Assert.Equal("SONY-CORP", resSpaces.Value.Items[0].Code);

        // 2. Search with tabs: "Sony\tCorporation" matches "Sony Corporation"
        var resTabs = await service.SearchPublicAsync(new Application.DTOs.Brands.GetBrandsRequest { Keyword = "Sony\tCorporation" });
        Assert.True(resTabs.IsSuccess);
        Assert.Single(resTabs.Value.Items);
        Assert.Equal("SONY-CORP", resTabs.Value.Items[0].Code);

        // 3. Search with decomposed NFD: "Mu\u0308ller" matches stored NFC "Müller"
        string nfdQuery = "Mu\u0308ller";
        Assert.False(nfdQuery.IsNormalized(System.Text.NormalizationForm.FormC));
        var resNfd = await service.SearchPublicAsync(new Application.DTOs.Brands.GetBrandsRequest { Keyword = nfdQuery });
        Assert.True(resNfd.IsSuccess);
        Assert.Single(resNfd.Value.Items);
        Assert.Equal("MUELLER", resNfd.Value.Items[0].Code);

        // 4. Code search preserves internal spaces: "SPECIAL   SPACES" matches Code
        var resCode = await service.SearchPublicAsync(new Application.DTOs.Brands.GetBrandsRequest { Keyword = "SPECIAL   SPACES" });
        Assert.True(resCode.IsSuccess);
        Assert.Single(resCode.Value.Items);
        Assert.Equal("SPECIAL   SPACES", resCode.Value.Items[0].Code);
    }

    private sealed class DummyMediaClient : Application.Clients.IMediaServiceClient
    {
        public Task<Result<Application.DTOs.Media.MediaVerificationDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure<Application.DTOs.Media.MediaVerificationDto>(CatalogErrors.Media.NotFound));
        public Task<Result> SetVisibilityAsync(Guid mediaId, string visibility, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public async Task Search_SortingAndPagination_WithStableIdTieBreaker()
    {
        var b1 = new Brand
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Code = "BRAND-B",
            Name = "Bravo",
            Slug = "bravo",
            CreatedAt = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc)
        };
        var b2 = new Brand
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Code = "BRAND-A",
            Name = "Alpha",
            Slug = "alpha",
            CreatedAt = new DateTime(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc)
        };
        var b3 = new Brand
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000003"),
            Code = "BRAND-C",
            Name = "Charlie",
            Slug = "charlie",
            CreatedAt = new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc)
        };

        await repository.AddAsync(b1);
        await repository.AddAsync(b2);
        await repository.AddAsync(b3);

        // Sort name_asc
        var nameAsc = await repository.SearchAsync(new BrandSearchQuery { Sort = "name_asc" });
        Assert.Equal(["Alpha", "Bravo", "Charlie"], nameAsc.Items.Select(i => i.Name));

        // Sort name_desc
        var nameDesc = await repository.SearchAsync(new BrandSearchQuery { Sort = "name_desc" });
        Assert.Equal(["Charlie", "Bravo", "Alpha"], nameDesc.Items.Select(i => i.Name));

        // Sort newest (default)
        var newest = await repository.SearchAsync(new BrandSearchQuery { Sort = "newest" });
        Assert.Equal(["Charlie", "Alpha", "Bravo"], newest.Items.Select(i => i.Name));

        // Pagination
        var page1 = await repository.SearchAsync(new BrandSearchQuery { Sort = "name_asc", Page = 1, PageSize = 2 });
        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(["Alpha", "Bravo"], page1.Items.Select(i => i.Name));

        var page2 = await repository.SearchAsync(new BrandSearchQuery { Sort = "name_asc", Page = 2, PageSize = 2 });
        Assert.Single(page2.Items);
        Assert.Equal("Charlie", page2.Items[0].Name);
    }

    [Fact]
    public async Task Search_PublicVsAdmin_ActiveFiltering()
    {
        var activeBrand = new Brand
        {
            Id = Guid.NewGuid(),
            Code = "ACTIVE-1",
            Name = "Active Brand",
            Slug = "active-brand",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var inactiveBrand = new Brand
        {
            Id = Guid.NewGuid(),
            Code = "INACTIVE-1",
            Name = "Inactive Brand",
            Slug = "inactive-brand",
            IsActive = false,
            CreatedAt = DateTime.UtcNow
        };

        await repository.AddAsync(activeBrand);
        await repository.AddAsync(inactiveBrand);

        // 1. Public search: IsActive = true
        var publicList = await repository.SearchAsync(new BrandSearchQuery { IsActive = true });
        Assert.Contains(publicList.Items, b => b.Code == "ACTIVE-1");
        Assert.DoesNotContain(publicList.Items, b => b.Code == "INACTIVE-1");

        // 2. Admin search không lọc: IsActive = null -> lấy cả 2
        var adminAll = await repository.SearchAsync(new BrandSearchQuery { IsActive = null });
        Assert.Contains(adminAll.Items, b => b.Code == "ACTIVE-1");
        Assert.Contains(adminAll.Items, b => b.Code == "INACTIVE-1");

        // 3. Admin search lọc isActive = false
        var adminInactive = await repository.SearchAsync(new BrandSearchQuery { IsActive = false });
        Assert.DoesNotContain(adminInactive.Items, b => b.Code == "ACTIVE-1");
        Assert.Contains(adminInactive.Items, b => b.Code == "INACTIVE-1");

        // 4. Public GetActiveById: inactive trả null
        var pubActive = await repository.GetActiveByIdAsync(activeBrand.Id);
        Assert.NotNull(pubActive);
        var pubInactive = await repository.GetActiveByIdAsync(inactiveBrand.Id);
        Assert.Null(pubInactive);

        // 5. Admin GetById: đọc cả hai
        var admActive = await repository.GetByIdAsync(activeBrand.Id);
        Assert.NotNull(admActive);
        var admInactive = await repository.GetByIdAsync(inactiveBrand.Id);
        Assert.NotNull(admInactive);
    }
}
