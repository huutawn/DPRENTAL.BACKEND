using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.CatalogService.Application.Clients;
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

namespace Dprental.CatalogService.Api.Tests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class EquipmentTypeWriteEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;
    private readonly CatalogServiceDbContext dbContext;
    private readonly FakeMediaVerificationClient mediaClient = new();

    public EquipmentTypeWriteEndpointTests()
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

    private static string AdminToken => TestJwtHelper.GenerateToken(
        sub: Guid.NewGuid().ToString(),
        sid: Guid.NewGuid().ToString(),
        role: "adm");

    private static string CustomerToken => TestJwtHelper.GenerateToken(
        sub: Guid.NewGuid().ToString(),
        sid: Guid.NewGuid().ToString(),
        role: "cus");

    [Fact]
    public async Task WriteEndpoints_Anonymous_ReturnsUnauthorized401()
    {
        Guid id = Guid.NewGuid();

        var postRes = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, postRes.StatusCode);

        var patchRes = await client.PatchAsync($"/api/v1/catalog/equipment-types/{id}", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, patchRes.StatusCode);

        var pubRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{id}/publish", null);
        Assert.Equal(HttpStatusCode.Unauthorized, pubRes.StatusCode);

        var disRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{id}/disable", null);
        Assert.Equal(HttpStatusCode.Unauthorized, disRes.StatusCode);
    }

    [Fact]
    public async Task WriteEndpoints_NonAdmin_ReturnsForbidden403()
    {
        Guid id = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CustomerToken);

        var postRes = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", new { });
        Assert.Equal(HttpStatusCode.Forbidden, postRes.StatusCode);

        var patchRes = await client.PatchAsync($"/api/v1/catalog/equipment-types/{id}", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, patchRes.StatusCode);

        var pubRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{id}/publish", null);
        Assert.Equal(HttpStatusCode.Forbidden, pubRes.StatusCode);

        var disRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{id}/disable", null);
        Assert.Equal(HttpStatusCode.Forbidden, disRes.StatusCode);
    }

    [Fact]
    public async Task Create_Valid_Returns201Created_WithDraftStatus()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Drums", isActive: true);

        Guid mediaId = Guid.NewGuid();
        var request = new CreateEquipmentTypeRequest(
            code: "DRUM-001",
            name: "Bộ trống Jazz",
            description: "Bộ trống chất lượng cao",
            categoryId: category.Id,
            media: [new EquipmentTypeMediaItemDto(mediaId, 0, true)]
        );

        var response = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var detail = await response.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(detail);
        Assert.Equal(EquipmentTypeStatus.Draft, detail.Status);
        Assert.Equal("DRUM-001", detail.Code);
        Assert.Equal("Bộ trống Jazz", detail.Name);
        Assert.Null(detail.PublishedAt);
        Assert.Single(detail.MediaIds);
        Assert.Equal(mediaId, detail.MediaIds[0]);
    }

    [Fact]
    public async Task Create_ExtraJsonFields_Returns400BadRequest()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Keyboards", isActive: true);

        string json = $$"""
            {
                "code": "KEY-001",
                "name": "Keyboard Yamaha",
                "categoryId": "{{category.Id}}",
                "brand": "Yamaha",
                "dailyPrice": 500000
            }
            """;

        var response = await client.PostAsync(
            "/api/v1/catalog/equipment-types",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_MissingOrInactiveCategory_Returns400BadRequest()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);

        // Missing Category
        var missingCatReq = new CreateEquipmentTypeRequest("EQ-MISSING", "Name", null, Guid.NewGuid(), null);
        var missingRes = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", missingCatReq);
        Assert.Equal(HttpStatusCode.BadRequest, missingRes.StatusCode);

        // Inactive Category
        Category inactiveCat = await CreateCategoryAsync("Inactive Cat", isActive: false);
        var inactiveCatReq = new CreateEquipmentTypeRequest("EQ-INACTIVE", "Name", null, inactiveCat.Id, null);
        var inactiveRes = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", inactiveCatReq);
        Assert.Equal(HttpStatusCode.BadRequest, inactiveRes.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateCode_Returns409Conflict()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Monitors", isActive: true);

        var req1 = new CreateEquipmentTypeRequest("MON-001", "Monitor A", null, category.Id, null);
        var res1 = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", req1);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);

        var req2 = new CreateEquipmentTypeRequest("MON-001", "Monitor B", null, category.Id, null);
        var res2 = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", req2);
        Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);
    }

    [Fact]
    public async Task Patch_EmptyBody_OrDisallowedFields_Returns400BadRequest()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Mixers", isActive: true);
        EquipmentType eq = await CreateEquipmentTypeAsync("MIX-001", "Mixer", category.Id);

        // Empty PATCH {}
        var emptyRes = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, emptyRes.StatusCode);

        // Disallowed field: status
        var disallowedRes = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent("""{"status": "Published"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, disallowedRes.StatusCode);
    }

    [Fact]
    public async Task Patch_ExplicitNullOnMandatoryFields_Returns400BadRequest()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Guitars", isActive: true);
        EquipmentType eq = await CreateEquipmentTypeAsync("GUI-001", "Guitar", category.Id);

        var nullCodeRes = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent("""{"code": null}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, nullCodeRes.StatusCode);

        var nullNameRes = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent("""{"name": null}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, nullNameRes.StatusCode);

        var nullCatRes = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent("""{"categoryId": null}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, nullCatRes.StatusCode);
    }

    [Fact]
    public async Task Patch_DescriptionNull_ClearsDescription_WhileOmissionPreservesOtherFields()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Pianos", isActive: true);
        EquipmentType eq = await CreateEquipmentTypeAsync("PIA-001", "Piano Grand", category.Id, description: "Mô tả ban đầu");

        string patchJson = """{"description": null}""";
        var res = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent(patchJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var detail = await res.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(detail);
        Assert.Null(detail.Description);
        Assert.Equal("PIA-001", detail.Code);
        Assert.Equal("Piano Grand", detail.Name);
    }

    [Fact]
    public async Task Patch_MediaReplacement_AndEmptyMediaClearsAll()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Synths", isActive: true);
        EquipmentType eq = await CreateEquipmentTypeAsync("SYN-001", "Synth", category.Id);

        Guid m1 = Guid.NewGuid();
        Guid m2 = Guid.NewGuid();

        // Thêm 2 media
        string patchWithMedia = $$"""
            {
                "media": [
                    { "mediaId": "{{m1}}", "displayOrder": 0, "isThumbnail": true },
                    { "mediaId": "{{m2}}", "displayOrder": 1, "isThumbnail": false }
                ]
            }
            """;

        var res1 = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent(patchWithMedia, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        var detail1 = await res1.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(detail1);
        Assert.Equal(2, detail1.MediaIds.Count);

        // Xóa hết media bằng "media": []
        string patchEmptyMedia = """{"media": []}""";
        var res2 = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent(patchEmptyMedia, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var detail2 = await res2.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(detail2);
        Assert.Empty(detail2.MediaIds);
    }
    [Fact]
    public async Task Create_WithNullMediaElement_Returns400InvalidMedia()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Violins", isActive: true);

        string json = $$"""
            {
                "code": "VIO-001",
                "name": "Violin Classic",
                "categoryId": "{{category.Id}}",
                "media": [null]
            }
            """;

        var response = await client.PostAsync(
            "/api/v1/catalog/equipment-types",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EquipmentType.InvalidMedia", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Patch_WithNullMediaElement_Returns400InvalidMedia()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Cellos", isActive: true);
        EquipmentType eq = await CreateEquipmentTypeAsync("CEL-001", "Cello", category.Id);

        string json = """{"media": [null]}""";
        var response = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EquipmentType.InvalidMedia", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Create_WithExplicitNullMedia_Returns400InvalidMedia()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Flutes", isActive: true);

        string json = $$"""
            {
                "code": "FLU-001",
                "name": "Flute Concert",
                "categoryId": "{{category.Id}}",
                "media": null
            }
            """;

        var response = await client.PostAsync(
            "/api/v1/catalog/equipment-types",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EquipmentType.InvalidMedia", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Patch_WithExplicitNullMedia_Returns400InvalidMedia()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Clarinets", isActive: true);
        EquipmentType eq = await CreateEquipmentTypeAsync("CLA-001", "Clarinet", category.Id);

        string json = """{"media": null}""";
        var response = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EquipmentType.InvalidMedia", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Create_WithEmptyGuidMediaId_Returns400InvalidMedia()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Trumpets", isActive: true);

        string json = $$"""
            {
                "code": "TRU-001",
                "name": "Trumpet",
                "categoryId": "{{category.Id}}",
                "media": [
                    { "mediaId": "00000000-0000-0000-0000-000000000000", "displayOrder": 0, "isThumbnail": true }
                ]
            }
            """;

        var response = await client.PostAsync(
            "/api/v1/catalog/equipment-types",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EquipmentType.InvalidMedia", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Create_WithDuplicateMediaId_Returns400InvalidMedia()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Horns", isActive: true);
        Guid sharedMedia = Guid.NewGuid();

        string json = $$"""
            {
                "code": "HRN-001",
                "name": "French Horn",
                "categoryId": "{{category.Id}}",
                "media": [
                    { "mediaId": "{{sharedMedia}}", "displayOrder": 0, "isThumbnail": true },
                    { "mediaId": "{{sharedMedia}}", "displayOrder": 1, "isThumbnail": false }
                ]
            }
            """;

        var response = await client.PostAsync(
            "/api/v1/catalog/equipment-types",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EquipmentType.InvalidMedia", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Create_WithNegativeDisplayOrder_Returns400InvalidMedia()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Tubas", isActive: true);

        string json = $$"""
            {
                "code": "TUB-001",
                "name": "Tuba",
                "categoryId": "{{category.Id}}",
                "media": [
                    { "mediaId": "{{Guid.NewGuid()}}", "displayOrder": -1, "isThumbnail": true }
                ]
            }
            """;

        var response = await client.PostAsync(
            "/api/v1/catalog/equipment-types",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EquipmentType.InvalidMedia", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Create_WithMultipleThumbnails_Returns400InvalidMedia()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Oboes", isActive: true);

        string json = $$"""
            {
                "code": "OBO-001",
                "name": "Oboe",
                "categoryId": "{{category.Id}}",
                "media": [
                    { "mediaId": "{{Guid.NewGuid()}}", "displayOrder": 0, "isThumbnail": true },
                    { "mediaId": "{{Guid.NewGuid()}}", "displayOrder": 1, "isThumbnail": true }
                ]
            }
            """;

        var response = await client.PostAsync(
            "/api/v1/catalog/equipment-types",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EquipmentType.InvalidMedia", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Create_And_Patch_WithNestedUnknownJsonFields_Returns400BadRequest()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Harps", isActive: true);

        // Nested unknown in Create
        string createJson = $$"""
            {
                "code": "HRP-001",
                "name": "Concert Harp",
                "categoryId": "{{category.Id}}",
                "media": [
                    { "mediaId": "{{Guid.NewGuid()}}", "displayOrder": 0, "isThumbnail": true, "extraNested": "notAllowed" }
                ]
            }
            """;

        var createRes = await client.PostAsync(
            "/api/v1/catalog/equipment-types",
            new StringContent(createJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, createRes.StatusCode);

        // Nested unknown in Patch
        EquipmentType eq = await CreateEquipmentTypeAsync("HRP-002", "Harp 2", category.Id);
        string patchJson = $$"""
            {
                "media": [
                    { "mediaId": "{{Guid.NewGuid()}}", "displayOrder": 0, "isThumbnail": true, "extraNested": 999 }
                ]
            }
            """;

        var patchRes = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent(patchJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, patchRes.StatusCode);
    }

    [Fact]
    public async Task Create_WithOmittedMedia_DefaultsToEmptyAndSucceeds()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Ukuleles", isActive: true);

        string json = $$"""
            {
                "code": "UKU-001",
                "name": "Ukulele Soprano",
                "categoryId": "{{category.Id}}"
            }
            """;

        var response = await client.PostAsync(
            "/api/v1/catalog/equipment-types",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(detail);
        Assert.Empty(detail.MediaIds);
    }

    [Fact]
    public async Task Publish_LifecycleTransitions_AndCategoryInvariant()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category activeCat = await CreateCategoryAsync("Speakers", isActive: true);
        Category inactiveCat = await CreateCategoryAsync("Inactive Speakers", isActive: false);

        EquipmentType draft = await CreateEquipmentTypeAsync("SPK-001", "Speaker", activeCat.Id);

        // 1. Publish từ Draft -> OK 200
        var pubRes1 = await client.PostAsync($"/api/v1/catalog/equipment-types/{draft.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, pubRes1.StatusCode);
        var detailPub = await pubRes1.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(detailPub);
        Assert.Equal(EquipmentTypeStatus.Published, detailPub.Status);
        Assert.NotNull(detailPub.PublishedAt);

        // 2. Publish khi đã Published -> 409 Conflict
        var pubRes2 = await client.PostAsync($"/api/v1/catalog/equipment-types/{draft.Id}/publish", null);
        Assert.Equal(HttpStatusCode.Conflict, pubRes2.StatusCode);

        // 3. Publish khi Category inactive -> 400
        EquipmentType inactiveCatEq = await CreateEquipmentTypeAsync("SPK-002", "Speaker Inactive Cat", inactiveCat.Id);
        var pubInactiveRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{inactiveCatEq.Id}/publish", null);
        Assert.Equal(HttpStatusCode.BadRequest, pubInactiveRes.StatusCode);
    }

    [Fact]
    public async Task Disable_LifecycleTransitions_AndAllowsInactiveCategory()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Cables", isActive: true);

        // 1. Disable khi đang Draft -> 409 Conflict
        EquipmentType draft = await CreateEquipmentTypeAsync("CAB-001", "Cable Draft", category.Id);
        var disDraftRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{draft.Id}/disable", null);
        Assert.Equal(HttpStatusCode.Conflict, disDraftRes.StatusCode);

        // 2. Publish rồi Disable -> 200 OK
        await client.PostAsync($"/api/v1/catalog/equipment-types/{draft.Id}/publish", null);

        var disPubRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{draft.Id}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disPubRes.StatusCode);
        var detailDis = await disPubRes.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(detailDis);
        Assert.Equal(EquipmentTypeStatus.Disabled, detailDis.Status);
        Assert.NotNull(detailDis.PublishedAt); // PublishedAt được giữ lại!

        // 3. Disable khi đã Disabled -> 409 Conflict
        var disAgainRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{draft.Id}/disable", null);
        Assert.Equal(HttpStatusCode.Conflict, disAgainRes.StatusCode);

        // 4. Disable vẫn được phép khi Category inactive
        category.IsActive = false;
        await dbContext.SaveChangesAsync();

        EquipmentType pub2 = await CreateEquipmentTypeAsync("CAB-002", "Cable 2", category.Id);
        pub2.Publish(DateTime.UtcNow);
        await dbContext.SaveChangesAsync();

        var disInactiveCatRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{pub2.Id}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disInactiveCatRes.StatusCode);
    }

    private async Task<Category> CreateCategoryAsync(string name, bool isActive = true)
    {
        Category cat = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}",
            IsActive = isActive,
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
        string? description = null)
    {
        EquipmentType eq = new()
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = name,
            Description = description,
            CategoryId = categoryId,
            CreatedAt = DateTime.UtcNow
        };
        dbContext.EquipmentTypes.Add(eq);
        await dbContext.SaveChangesAsync();
        return eq;
    }
}
