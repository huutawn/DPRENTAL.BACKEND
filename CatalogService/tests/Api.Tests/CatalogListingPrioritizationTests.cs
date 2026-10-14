using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CatalogService.Application.DTOs.Category;
using Dprental.CatalogService.Application.Clients;
using Dprental.CatalogService.Application.DTOs.Brands;
using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Application.Validators;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using System.Security.Claims;
using Dprental.CatalogService.Api.Clients;
using Dprental.CatalogService.Common.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dprental.CatalogService.Api.Tests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed partial class CatalogListingPrioritizationTests : IAsyncLifetime, IDisposable
{
    private sealed class FakeInventoryClient : IInventoryServiceClient
    {
        public InventoryListingStatisticsDto Statistics { get; set; } = new(
            new Dictionary<Guid, int>(),
            new Dictionary<Guid, int>());

        public Error? ErrorToReturn { get; set; }
        public string? CapturedScope { get; private set; }
        public string? CapturedListingStatus { get; private set; }

        public Task<Result<InventoryListingStatisticsDto>> GetListingStatisticsAsync(
            string scope,
            string? listingStatus,
            CancellationToken cancellationToken = default)
        {
            CapturedScope = scope;
            CapturedListingStatus = listingStatus;

            if (ErrorToReturn != null)
            {
                return Task.FromResult(Result.Failure<InventoryListingStatisticsDto>(ErrorToReturn));
            }

            return Task.FromResult(Result.Success(Statistics));
        }
    }

    private sealed class TestMediaClient : IMediaServiceClient
    {
        public Task<Result<Application.DTOs.Media.MediaVerificationDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new Application.DTOs.Media.MediaVerificationDto(mediaId, "READY", Guid.NewGuid().ToString(), "image.jpg", "image/jpeg", 1024, DateTimeOffset.UtcNow)));

        public Task<Result> SetVisibilityAsync(Guid mediaId, string visibility, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly CatalogServiceDbContext _dbContext;
    private readonly FakeInventoryClient _inventoryClient = new();
    private readonly TestMediaClient _mediaClient = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public CatalogListingPrioritizationTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", PostgreSqlFixture.ConnectionString);
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseSetting("InventoryService:BaseUrl", "http://localhost:5003");
                builder.UseSetting("InventoryService:TimeoutSeconds", "3");
                builder.UseTestJwks();
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IMediaServiceClient>();
                    services.AddSingleton<IMediaServiceClient>(_mediaClient);

                    services.RemoveAll<IInventoryServiceClient>();
                    services.AddSingleton<IInventoryServiceClient>(_inventoryClient);
                });
            });

        _client = _factory.CreateClient();

        var options = new DbContextOptionsBuilder<CatalogServiceDbContext>()
            .UseNpgsql(PostgreSqlFixture.ConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable(
                    CatalogServiceDbContext.MigrationsHistoryTableName,
                    CatalogServiceDbContext.SchemaName))
            .Options;

        _dbContext = new CatalogServiceDbContext(options);
    }

    public async Task InitializeAsync()
    {
        await _dbContext.Database.MigrateAsync();
        await CleanDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await CleanDatabaseAsync();
        await _dbContext.DisposeAsync();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task CleanDatabaseAsync()
    {
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE catalog."EquipmentTypeMedia",
                           catalog."EquipmentTypeSpecSchemas",
                           catalog."EquipmentTypeCategories",
                           catalog."EquipmentTypes",
                           catalog."Categories",
                           catalog."Brands"
            CASCADE;
            """);
    }
    private async Task<EquipmentType> CreateAndPersistPublishedEquipmentTypeAsync(Guid id, string code, string name, Category? category = null)
    {
        var eq = new EquipmentType { Id = id, Code = code, Name = name };
        if (category != null)
        {
            eq.Categories.Add(category);
        }
        await _dbContext.EquipmentTypes.AddAsync(eq);
        await _dbContext.SaveChangesAsync();

        var schema = EquipmentTypeSpecSchema.CreatePublished(
            Guid.NewGuid(), eq.Id, 1, new EquipmentTypeSpecDefinition { Fields = [] }, eq.CreatedAt, eq.CreatedAt);
        _dbContext.EquipmentTypeSpecSchemas.Add(schema);
        eq.ApplyPublishedSchema(schema, eq.CreatedAt);
        await _dbContext.SaveChangesAsync();

        eq.Publish(DateTime.UtcNow);
        await _dbContext.SaveChangesAsync();

        return eq;
    }


    [Fact]
    public void StartupOptionsValidator_ValidatesBaseUrlAndTimeout()
    {
        var validator = new InventoryServiceOptionsValidator();

        var valid = new InventoryServiceOptions { BaseUrl = "http://localhost:5003", TimeoutSeconds = 3 };
        Assert.True(validator.Validate(null, valid).Succeeded);

        var emptyUrl = new InventoryServiceOptions { BaseUrl = "", TimeoutSeconds = 3 };
        Assert.True(validator.Validate(null, emptyUrl).Failed);

        var invalidTimeout = new InventoryServiceOptions { BaseUrl = "http://localhost:5003", TimeoutSeconds = 0 };
        Assert.True(validator.Validate(null, invalidTimeout).Failed);
    }

    [Fact]
    public void DiscoveryQueryValidationHelper_ValidatesScopeAndStatus()
    {
        // Public with listingStatus -> fails
        var res1 = DiscoveryQueryValidationHelper.ValidateScopeAndListingStatus("public", "Active");
        Assert.True(res1.IsFailure);

        // Mine with valid listingStatus -> succeeds
        var res2 = DiscoveryQueryValidationHelper.ValidateScopeAndListingStatus("mine", "Draft");
        Assert.True(res2.IsSuccess);

        // Mine with invalid listingStatus -> fails
        var res3 = DiscoveryQueryValidationHelper.ValidateScopeAndListingStatus("mine", "InvalidStatus");
        Assert.True(res3.IsFailure);

        // Invalid scope -> fails
        var res4 = DiscoveryQueryValidationHelper.ValidateScopeAndListingStatus("invalid_scope", null);
        Assert.True(res4.IsFailure);
    }

    [Fact]
    public async Task Categories_Discovery_OrdersByListingCountDescending_WithZeroAtEnd()
    {
        // Seed 3 categories
        var cat1 = new Category { Id = Guid.NewGuid(), Name = "Category A (5 items)", Slug = "cat-a", IsActive = true };
        var cat2 = new Category { Id = Guid.NewGuid(), Name = "Category B (2 items)", Slug = "cat-b", IsActive = true };
        var cat3 = new Category { Id = Guid.NewGuid(), Name = "Category C (0 items)", Slug = "cat-c", IsActive = true };
        var catInactive = new Category { Id = Guid.NewGuid(), Name = "Category Inactive", Slug = "cat-inactive", IsActive = false };

        await _dbContext.Categories.AddRangeAsync(cat1, cat2, cat3, catInactive);
        await _dbContext.SaveChangesAsync();

        var type1 = await CreateAndPersistPublishedEquipmentTypeAsync(Guid.NewGuid(), "TYPE1", "Type 1", cat1);
        var type2 = await CreateAndPersistPublishedEquipmentTypeAsync(Guid.NewGuid(), "TYPE2", "Type 2", cat2);
        var type3 = await CreateAndPersistPublishedEquipmentTypeAsync(Guid.NewGuid(), "TYPE3", "Type 3", cat3);

        // Inventory counts: type1 has 5, type2 has 2, type3 has 0
        _inventoryClient.Statistics = new InventoryListingStatisticsDto(
            new Dictionary<Guid, int>
            {
                [type1.Id] = 5,
                [type2.Id] = 2,
                [type3.Id] = 0
            },
            new Dictionary<Guid, int>());

        var response = await _client.GetAsync("/api/v1/catalog/categories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var categories = await response.Content.ReadFromJsonAsync<List<CategoryDetailResponse>>(JsonOptions);
        Assert.NotNull(categories);
        Assert.Equal(3, categories.Count); // Inactive category excluded

        // Default order: 5, 2, 0
        Assert.Equal(cat1.Id, categories[0].Id);
        Assert.Equal(5, categories[0].ListingCount);

        Assert.Equal(cat2.Id, categories[1].Id);
        Assert.Equal(2, categories[1].ListingCount);

        Assert.Equal(cat3.Id, categories[2].Id);
        Assert.Equal(0, categories[2].ListingCount);
        // Check sort=listing_count_asc: counts > 0 ascending (2, 5), and 0 at the end (0)
        var ascResponse = await _client.GetAsync("/api/v1/catalog/categories?sort=listing_count_asc");
        Assert.Equal(HttpStatusCode.OK, ascResponse.StatusCode);
        var ascCategories = await ascResponse.Content.ReadFromJsonAsync<List<CategoryDetailResponse>>(JsonOptions);
        Assert.NotNull(ascCategories);
        Assert.Equal(cat2.Id, ascCategories[0].Id);
        Assert.Equal(2, ascCategories[0].ListingCount);
        Assert.Equal(cat1.Id, ascCategories[1].Id);
        Assert.Equal(5, ascCategories[1].ListingCount);
        Assert.Equal(cat3.Id, ascCategories[2].Id);
        Assert.Equal(0, ascCategories[2].ListingCount);

        // Detail endpoint: listingCount is NOT populated with fake count
        var detailResponse = await _client.GetAsync($"/api/v1/catalog/categories/{cat1.Id}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detailRaw = await detailResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"listingCount\"", detailRaw);
    }

    [Fact]
    public async Task Categories_Discovery_PublicWithListingStatus_Returns400()
    {
        var response = await _client.GetAsync("/api/v1/catalog/categories?scope=public&listingStatus=Active");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Categories_Discovery_MineWithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/catalog/categories?scope=mine");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Categories_Discovery_WhenInventoryFails_Returns503()
    {
        _inventoryClient.ErrorToReturn = Error.Unavailable(
            CatalogErrorCode.Inventory.ServiceUnavailable,
            "InventoryService is down");

        var response = await _client.GetAsync("/api/v1/catalog/categories");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Brands_Discovery_OrdersByListingCountDescending_AndExcludesInactive()
    {
        var brand1 = new Brand { Id = Guid.NewGuid(), Code = "B1", Name = "Brand 1 (5 items)", Slug = "b1", IsActive = true };
        var brand2 = new Brand { Id = Guid.NewGuid(), Code = "B2", Name = "Brand 2 (2 items)", Slug = "b2", IsActive = true };
        var brand3 = new Brand { Id = Guid.NewGuid(), Code = "B3", Name = "Brand 3 (0 items)", Slug = "b3", IsActive = true };
        var brandInactive = new Brand { Id = Guid.NewGuid(), Code = "B4", Name = "Brand Inactive", Slug = "b4", IsActive = false };

        await _dbContext.Brands.AddRangeAsync(brand1, brand2, brand3, brandInactive);
        await _dbContext.SaveChangesAsync();

        _inventoryClient.Statistics = new InventoryListingStatisticsDto(
            new Dictionary<Guid, int>(),
            new Dictionary<Guid, int>
            {
                [brand1.Id] = 5,
                [brand2.Id] = 2,
                [brand3.Id] = 0,
                [brandInactive.Id] = 100 // Inactive brand must not appear
            });

        var response = await _client.GetAsync("/api/v1/catalog/brands");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<PagedResult<BrandDto>>(JsonOptions);
        Assert.NotNull(paged);
        Assert.Equal(3, paged.TotalCount);

        Assert.Equal(brand1.Id, paged.Items[0].Id);
        Assert.Equal(5, paged.Items[0].ListingCount);

        Assert.Equal(brand2.Id, paged.Items[1].Id);
        Assert.Equal(2, paged.Items[1].ListingCount);

        Assert.Equal(brand3.Id, paged.Items[2].Id);
        Assert.Equal(0, paged.Items[2].ListingCount);
    }

    [Fact]
    public async Task EquipmentTypes_Discovery_OrdersByListingCountDescending_AndExcludesDraftDisabled()
    {
        var cat = new Category { Id = Guid.NewGuid(), Name = "Category Common", Slug = "cat-common", IsActive = true };
        await _dbContext.Categories.AddAsync(cat);
        await _dbContext.SaveChangesAsync();

        var type1 = await CreateAndPersistPublishedEquipmentTypeAsync(Guid.NewGuid(), "ET1", "Type 1 (5 items)", cat);
        var type2 = await CreateAndPersistPublishedEquipmentTypeAsync(Guid.NewGuid(), "ET2", "Type 2 (2 items)", cat);
        var type3 = await CreateAndPersistPublishedEquipmentTypeAsync(Guid.NewGuid(), "ET3", "Type 3 (0 items)", cat);

        var typeDisabled = await CreateAndPersistPublishedEquipmentTypeAsync(Guid.NewGuid(), "ET4", "Type Disabled", cat);
        typeDisabled.Disable(DateTime.UtcNow);
        await _dbContext.SaveChangesAsync();

        _inventoryClient.Statistics = new InventoryListingStatisticsDto(
            new Dictionary<Guid, int>
            {
                [type1.Id] = 5,
                [type2.Id] = 2,
                [type3.Id] = 0,
                [typeDisabled.Id] = 100 // Disabled type must not appear
            },
            new Dictionary<Guid, int>());

        var response = await _client.GetAsync("/api/v1/catalog/equipment-types");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<PagedResult<EquipmentTypeListItemDto>>(JsonOptions);
        Assert.NotNull(paged);
        Assert.Equal(3, paged.TotalCount);

        Assert.Equal(type1.Id, paged.Items[0].Id);
        Assert.Equal(5, paged.Items[0].ListingCount);

        Assert.Equal(type2.Id, paged.Items[1].Id);
        Assert.Equal(2, paged.Items[1].ListingCount);

        Assert.Equal(type3.Id, paged.Items[2].Id);
        Assert.Equal(0, paged.Items[2].ListingCount);
    }

    #region Multi-Page Database Pagination Tests

    [Fact]
    public async Task EquipmentTypes_DatabasePagination_MultiplePages_SortsCorrectlyAcrossPages()
    {
        var cat = new Category { Id = Guid.NewGuid(), Name = "Category Paginated", Slug = "cat-paged", IsActive = true };
        await _dbContext.Categories.AddAsync(cat);
        await _dbContext.SaveChangesAsync();

        var type1 = await CreateAndPersistPublishedEquipmentTypeAsync(Guid.NewGuid(), "P-ET1", "Type 1 (5 items)", cat);
        var type2 = await CreateAndPersistPublishedEquipmentTypeAsync(Guid.NewGuid(), "P-ET2", "Type 2 (2 items)", cat);
        var type3 = await CreateAndPersistPublishedEquipmentTypeAsync(Guid.NewGuid(), "P-ET3", "Type 3 (0 items)", cat);

        _inventoryClient.Statistics = new InventoryListingStatisticsDto(
            new Dictionary<Guid, int>
            {
                [type1.Id] = 5,
                [type2.Id] = 2,
                [type3.Id] = 0
            },
            new Dictionary<Guid, int>());

        // Page 1 with pageSize=2 -> should return type1 (5) and type2 (2)
        var responsePage1 = await _client.GetAsync("/api/v1/catalog/equipment-types?page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, responsePage1.StatusCode);
        var paged1 = await responsePage1.Content.ReadFromJsonAsync<PagedResult<EquipmentTypeListItemDto>>(JsonOptions);
        Assert.NotNull(paged1);
        Assert.Equal(3, paged1.TotalCount);
        Assert.Equal(2, paged1.Items.Count);
        Assert.Equal(type1.Id, paged1.Items[0].Id);
        Assert.Equal(5, paged1.Items[0].ListingCount);
        Assert.Equal(type2.Id, paged1.Items[1].Id);
        Assert.Equal(2, paged1.Items[1].ListingCount);

        // Page 2 with pageSize=2 -> should return type3 (0)
        var responsePage2 = await _client.GetAsync("/api/v1/catalog/equipment-types?page=2&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, responsePage2.StatusCode);
        var paged2 = await responsePage2.Content.ReadFromJsonAsync<PagedResult<EquipmentTypeListItemDto>>(JsonOptions);
        Assert.NotNull(paged2);
        Assert.Equal(3, paged2.TotalCount);
        Assert.Single(paged2.Items);
        Assert.Equal(type3.Id, paged2.Items[0].Id);
        Assert.Equal(0, paged2.Items[0].ListingCount);
    }

    #endregion
}
