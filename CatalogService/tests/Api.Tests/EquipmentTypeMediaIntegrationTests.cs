using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.CatalogService.Application.Clients;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

/// <summary>Kiểm tra việc gắn media vào equipment type qua Media Service giả, với PostgreSQL thật.</summary>
[Collection(PostgreSqlTestGroup.Name)]
public sealed class EquipmentTypeMediaIntegrationTests : IAsyncLifetime, IDisposable
{
    private const string BasePath = "/api/v1/catalog/equipment-types";

    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;
    private readonly CatalogServiceDbContext dbContext;
    private readonly FakeMediaVerificationClient mediaClient = new();

    public EquipmentTypeMediaIntegrationTests()
    {
        factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", PostgreSqlFixture.ConnectionString);
                builder.UseSetting("MediaService:BaseUrl", "http://media.test");
                builder.UseSetting("MediaService:ApiKey", "test-api-key");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseSetting("Auth:Jwt:Keys:0:KeyId", TestJwtHelper.DefaultKeyId);
                builder.UseSetting("Auth:Jwt:Keys:0:PublicKeyPem", TestJwtHelper.PublicKeyPem);

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IMediaVerificationClient>();
                    services.AddSingleton<IMediaVerificationClient>(mediaClient);
                });
            });

        client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestJwtHelper.GenerateToken(sub: Guid.NewGuid().ToString(), sid: Guid.NewGuid().ToString(), role: "adm"));

        var options = new DbContextOptionsBuilder<CatalogServiceDbContext>()
            .UseNpgsql(PostgreSqlFixture.ConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable(
                    CatalogServiceDbContext.MigrationsHistoryTableName,
                    CatalogServiceDbContext.SchemaName))
            .Options;

        dbContext = new CatalogServiceDbContext(options);
    }

    public async Task InitializeAsync()
    {
        await dbContext.Database.MigrateAsync();
        await dbContext.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"catalog\".\"EquipmentTypeMedia\", \"catalog\".\"EquipmentTypes\", \"catalog\".\"Categories\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        dbContext.Dispose();
    }

    [Theory]
    [InlineData(MediaVerificationStatus.NotFound, HttpStatusCode.BadRequest, CatalogErrorCode.EquipmentType.MediaNotFound)]
    [InlineData(MediaVerificationStatus.NotReady, HttpStatusCode.BadRequest, CatalogErrorCode.EquipmentType.MediaNotReady)]
    [InlineData(MediaVerificationStatus.Unavailable, HttpStatusCode.ServiceUnavailable, CatalogErrorCode.Catalog.MediaServiceUnavailable)]
    public async Task Create_WithUnusableMedia_IsRejectedAndNothingIsSaved(
        MediaVerificationStatus mediaStatus,
        HttpStatusCode expectedStatusCode,
        string expectedErrorCode)
    {
        Category category = await CreateCategoryAsync();
        Guid readyMedia = Guid.NewGuid();
        Guid badMedia = Guid.NewGuid();
        mediaClient.SetStatus(badMedia, mediaStatus);

        using HttpResponseMessage response = await client.PostAsJsonAsync(BasePath, new CreateEquipmentTypeRequest(
            code: "CAM-001",
            name: "Máy quay",
            description: null,
            categoryId: category.Id,
            media: [new EquipmentTypeMediaItemDto(readyMedia, 0, true), new EquipmentTypeMediaItemDto(badMedia, 1, false)]));

        await AssertProblemAsync(response, expectedStatusCode, expectedErrorCode);
        Assert.False(await dbContext.EquipmentTypes.AnyAsync());
        Assert.False(await dbContext.EquipmentTypeMedia.AnyAsync());
    }

    [Fact]
    public async Task Create_WhenOneMediaIsUnavailable_Returns503EvenIfAnotherIsNotFound()
    {
        Category category = await CreateCategoryAsync();
        Guid missingMedia = Guid.NewGuid();
        Guid unverifiableMedia = Guid.NewGuid();
        mediaClient.SetStatus(missingMedia, MediaVerificationStatus.NotFound);
        mediaClient.SetStatus(unverifiableMedia, MediaVerificationStatus.Unavailable);

        using HttpResponseMessage response = await client.PostAsJsonAsync(BasePath, new CreateEquipmentTypeRequest(
            code: "CAM-002",
            name: "Máy quay",
            description: null,
            categoryId: category.Id,
            media: [new EquipmentTypeMediaItemDto(missingMedia, 0, false), new EquipmentTypeMediaItemDto(unverifiableMedia, 1, false)]));

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, CatalogErrorCode.Catalog.MediaServiceUnavailable);
    }

    [Fact]
    public async Task Create_WithReadyMedia_VerifiesEveryMediaAndStoresOnlyReferences()
    {
        Category category = await CreateCategoryAsync();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        using HttpResponseMessage response = await client.PostAsJsonAsync(BasePath, new CreateEquipmentTypeRequest(
            code: "CAM-003",
            name: "Máy quay",
            description: null,
            categoryId: category.Id,
            media: [new EquipmentTypeMediaItemDto(first, 0, false), new EquipmentTypeMediaItemDto(second, 1, true)]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(new[] { first, second }.Order(), mediaClient.VerifiedMediaIds.Order());
        List<EquipmentTypeMedia> stored = await dbContext.EquipmentTypeMedia.AsNoTracking().OrderBy(m => m.DisplayOrder).ToListAsync();
        Assert.Equal([first, second], stored.Select(m => m.MediaId));
    }

    [Fact]
    public async Task Create_WithMoreThanMaxMedia_Returns400WithoutCallingMediaService()
    {
        Category category = await CreateCategoryAsync();
        List<EquipmentTypeMediaItemDto?> media = Enumerable.Range(0, 21)
            .Select(index => (EquipmentTypeMediaItemDto?)new EquipmentTypeMediaItemDto(Guid.NewGuid(), index, false))
            .ToList();

        using HttpResponseMessage response = await client.PostAsJsonAsync(BasePath, new CreateEquipmentTypeRequest(
            code: "CAM-004",
            name: "Máy quay",
            description: null,
            categoryId: category.Id,
            media: media));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, CatalogErrorCode.EquipmentType.InvalidMedia);
        Assert.Empty(mediaClient.VerifiedMediaIds);
    }

    [Fact]
    public async Task Create_WithInvalidCategory_DoesNotCallMediaService()
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(BasePath, new CreateEquipmentTypeRequest(
            code: "CAM-005",
            name: "Máy quay",
            description: null,
            categoryId: Guid.NewGuid(),
            media: [new EquipmentTypeMediaItemDto(Guid.NewGuid(), 0, true)]));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, CatalogErrorCode.EquipmentType.CategoryNotFound);
        Assert.Empty(mediaClient.VerifiedMediaIds);
    }

    [Theory]
    [InlineData(MediaVerificationStatus.NotFound, HttpStatusCode.BadRequest, CatalogErrorCode.EquipmentType.MediaNotFound)]
    [InlineData(MediaVerificationStatus.NotReady, HttpStatusCode.BadRequest, CatalogErrorCode.EquipmentType.MediaNotReady)]
    [InlineData(MediaVerificationStatus.Unavailable, HttpStatusCode.ServiceUnavailable, CatalogErrorCode.Catalog.MediaServiceUnavailable)]
    public async Task Update_AddingUnusableMedia_IsRejectedAndKeepsExistingState(
        MediaVerificationStatus mediaStatus,
        HttpStatusCode expectedStatusCode,
        string expectedErrorCode)
    {
        Guid existingMedia = Guid.NewGuid();
        EquipmentTypeDetailDto created = await CreateEquipmentTypeAsync("CAM-010", new EquipmentTypeMediaItemDto(existingMedia, 0, true));
        Guid badMedia = Guid.NewGuid();
        mediaClient.SetStatus(badMedia, mediaStatus);

        using HttpResponseMessage response = await PatchAsync(created.Id, $$"""
            {
                "name": "Tên mới",
                "media": [
                    { "mediaId": "{{existingMedia}}", "displayOrder": 1, "isThumbnail": false },
                    { "mediaId": "{{badMedia}}", "displayOrder": 0, "isThumbnail": true }
                ]
            }
            """);

        await AssertProblemAsync(response, expectedStatusCode, expectedErrorCode);
        EquipmentType stored = await dbContext.EquipmentTypes.AsNoTracking().Include(e => e.Media).SingleAsync(e => e.Id == created.Id);
        Assert.Equal(created.Name, stored.Name);
        EquipmentTypeMedia storedMedia = Assert.Single(stored.Media);
        Assert.Equal(existingMedia, storedMedia.MediaId);
        Assert.Equal(0, storedMedia.DisplayOrder);
        Assert.True(storedMedia.IsThumbnail);
    }

    [Fact]
    public async Task Update_VerifiesOnlyNewlyAddedMedia()
    {
        Guid existingMedia = Guid.NewGuid();
        EquipmentTypeDetailDto created = await CreateEquipmentTypeAsync("CAM-011", new EquipmentTypeMediaItemDto(existingMedia, 0, true));
        // Media đã gắn từ trước nay đã bị xóa bên Media Service: không được chặn thao tác của admin.
        mediaClient.SetStatus(existingMedia, MediaVerificationStatus.NotFound);
        mediaClient.ClearVerifiedMediaIds();
        Guid newMedia = Guid.NewGuid();

        using HttpResponseMessage response = await PatchAsync(created.Id, $$"""
            {
                "media": [
                    { "mediaId": "{{existingMedia}}", "displayOrder": 1, "isThumbnail": false },
                    { "mediaId": "{{newMedia}}", "displayOrder": 0, "isThumbnail": true }
                ]
            }
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(newMedia, Assert.Single(mediaClient.VerifiedMediaIds));
        EquipmentTypeDetailDto? detail = await response.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(detail);
        Assert.Equal([newMedia, existingMedia], detail.MediaIds);
    }

    [Theory]
    [InlineData("""{ "name": "Chỉ đổi tên" }""")]
    [InlineData("""{ "media": [] }""")]
    public async Task Update_WithoutNewMedia_DoesNotCallMediaService(string json)
    {
        EquipmentTypeDetailDto created = await CreateEquipmentTypeAsync("CAM-012", new EquipmentTypeMediaItemDto(Guid.NewGuid(), 0, true));
        mediaClient.ClearVerifiedMediaIds();
        mediaClient.DefaultStatus = MediaVerificationStatus.Unavailable;

        using HttpResponseMessage response = await PatchAsync(created.Id, json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(mediaClient.VerifiedMediaIds);
    }

    [Fact]
    public async Task Thumbnail_ExplicitFlagWinsOverDisplayOrder_AndListDetailAndWriteResponseAgree()
    {
        Guid first = Guid.NewGuid();
        Guid flagged = Guid.NewGuid();

        EquipmentTypeDetailDto created = await CreateEquipmentTypeAsync(
            "CAM-020",
            new EquipmentTypeMediaItemDto(first, 0, false),
            new EquipmentTypeMediaItemDto(flagged, 5, true));

        Assert.Equal(flagged, created.ThumbnailMediaId);
        await AssertPublishedThumbnailAsync(created.Id, flagged);
    }

    [Fact]
    public async Task Thumbnail_WithoutFlag_FallsBackToLowestDisplayOrder_AndListDetailAndWriteResponseAgree()
    {
        Guid later = Guid.NewGuid();
        Guid earliest = Guid.NewGuid();

        EquipmentTypeDetailDto created = await CreateEquipmentTypeAsync(
            "CAM-021",
            new EquipmentTypeMediaItemDto(later, 3, false),
            new EquipmentTypeMediaItemDto(earliest, 1, false));

        Assert.Equal(earliest, created.ThumbnailMediaId);
        await AssertPublishedThumbnailAsync(created.Id, earliest);
    }

    [Fact]
    public async Task Thumbnail_WithoutMedia_IsNull()
    {
        EquipmentTypeDetailDto created = await CreateEquipmentTypeAsync("CAM-022");

        Assert.Null(created.ThumbnailMediaId);
        await AssertPublishedThumbnailAsync(created.Id, null);
    }

    [Fact]
    public async Task Thumbnail_FollowsUpdate_WhenFlagMovesToAnotherMedia()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        EquipmentTypeDetailDto created = await CreateEquipmentTypeAsync(
            "CAM-023",
            new EquipmentTypeMediaItemDto(first, 0, true),
            new EquipmentTypeMediaItemDto(second, 1, false));

        using HttpResponseMessage response = await PatchAsync(created.Id, $$"""
            {
                "media": [
                    { "mediaId": "{{first}}", "displayOrder": 0, "isThumbnail": false },
                    { "mediaId": "{{second}}", "displayOrder": 1, "isThumbnail": true }
                ]
            }
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        EquipmentTypeDetailDto? updated = await response.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(updated);
        Assert.Equal(second, updated.ThumbnailMediaId);
        await AssertPublishedThumbnailAsync(created.Id, second);
    }

    /// <summary>Publish rồi đọc lại qua API public: danh sách và chi tiết phải chọn cùng một thumbnail.</summary>
    private async Task AssertPublishedThumbnailAsync(Guid equipmentTypeId, Guid? expected)
    {
        using HttpResponseMessage publishResponse = await client.PostAsync($"{BasePath}/{equipmentTypeId}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        EquipmentTypeDetailDto? published = await publishResponse.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(published);
        Assert.Equal(expected, published.ThumbnailMediaId);

        EquipmentTypeDetailDto? detail = await client.GetFromJsonAsync<EquipmentTypeDetailDto>($"{BasePath}/{equipmentTypeId}");
        Assert.NotNull(detail);
        Assert.Equal(expected, detail.ThumbnailMediaId);

        PagedResult<EquipmentTypeListItemDto>? list = await client.GetFromJsonAsync<PagedResult<EquipmentTypeListItemDto>>(BasePath);
        Assert.NotNull(list);
        EquipmentTypeListItemDto item = Assert.Single(list.Items, i => i.Id == equipmentTypeId);
        Assert.Equal(expected, item.ThumbnailMediaId);
    }

    private async Task<EquipmentTypeDetailDto> CreateEquipmentTypeAsync(string code, params EquipmentTypeMediaItemDto[] media)
    {
        Category category = await CreateCategoryAsync();

        using HttpResponseMessage response = await client.PostAsJsonAsync(BasePath, new CreateEquipmentTypeRequest(
            code: code,
            name: "Máy quay",
            description: null,
            categoryId: category.Id,
            media: media));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        EquipmentTypeDetailDto? detail = await response.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(detail);
        return detail;
    }

    private Task<HttpResponseMessage> PatchAsync(Guid equipmentTypeId, string json) =>
        client.PatchAsync($"{BasePath}/{equipmentTypeId}", new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<Category> CreateCategoryAsync()
    {
        Category category = new()
        {
            Id = Guid.NewGuid(),
            Name = "Camera",
            Slug = $"camera-{Guid.NewGuid():N}",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        dbContext.Categories.Add(category);
        await dbContext.SaveChangesAsync();
        return category;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatusCode, string expectedErrorCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedErrorCode, problem.GetProperty("title").GetString());
    }
}
