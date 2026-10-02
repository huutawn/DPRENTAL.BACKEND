using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.CatalogService.Application.Clients;
using CatalogService.Application.DTOs.Category;
using Dprental.CatalogService.Application.DTOs.Media;
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

namespace Dprental.CatalogService.Api.Tests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class EquipmentTypeWriteEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;
    private readonly CatalogServiceDbContext dbContext;

    public TestMediaServiceClient TestMediaClient { get; } = new();

    public static readonly Guid AdminUserGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static string CurrentAdminUserId => AdminUserGuid.ToString();

    public EquipmentTypeWriteEndpointTests()
    {
        factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", PostgreSqlFixture.ConnectionString);
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseTestJwks();
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IMediaServiceClient>();
                    services.AddSingleton<IMediaServiceClient>(TestMediaClient);
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
        await dbContext.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"catalog\".\"EquipmentTypeCategories\", \"catalog\".\"EquipmentTypeMedia\", \"catalog\".\"EquipmentTypes\", \"catalog\".\"Categories\" CASCADE;");
        TestMediaClient.Reset();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        dbContext.Dispose();
    }

    private static string AdminToken => TestJwtHelper.GenerateToken(
        sub: CurrentAdminUserId,
        sid: Guid.NewGuid().ToString(),
        role: "adm");

    private static string CustomerToken => TestJwtHelper.GenerateToken(
        sub: Guid.NewGuid().ToString(),
        sid: Guid.NewGuid().ToString(),
        role: "cus");

    private static string OwnerToken(params Guid[] organizationIds) => TestJwtHelper.GenerateToken(
        sub: Guid.NewGuid().ToString(),
        sid: Guid.NewGuid().ToString(),
        role: "own",
        organizationIds: organizationIds);

    private void Authenticate(string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<Guid> SeedEquipmentTypeAsync(string code, Guid? organizationId)
    {
        Category category = await CreateCategoryAsync($"Category {code}", isActive: true);
        EquipmentType equipmentType = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Code = code,
            Name = $"Thiết bị {code}",
            CreatedAt = DateTime.UtcNow,
            Categories = [category]
        };
        dbContext.EquipmentTypes.Add(equipmentType);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        return equipmentType.Id;
    }

    private Task<Guid?> ReadOrganizationIdAsync(Guid equipmentTypeId) =>
        dbContext.EquipmentTypes.AsNoTracking()
            .Where(e => e.Id == equipmentTypeId)
            .Select(e => e.OrganizationId)
            .SingleAsync();

    private static StringContent PatchName(string name) =>
        new(JsonSerializer.Serialize(new { name }), Encoding.UTF8, "application/json");

    [Fact]
    public async Task Owner_SingleOrganization_Create_TakesOrganizationFromToken()
    {
        Guid organizationId = Guid.NewGuid();
        Authenticate(OwnerToken(organizationId));
        Category category = await CreateCategoryAsync("Org Single", isActive: true);

        var response = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types",
            new CreateEquipmentTypeRequest { Code = "ORG-001", Name = "Loa", CategoryIds = [category.Id] });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.Equal(organizationId, await ReadOrganizationIdAsync(detail!.Id));
    }

    [Fact]
    public async Task Owner_Create_WithOrganizationOutsideToken_Returns403()
    {
        Authenticate(OwnerToken(Guid.NewGuid()));
        Category category = await CreateCategoryAsync("Org Foreign", isActive: true);

        var response = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types",
            new CreateEquipmentTypeRequest { Code = "ORG-002", Name = "Loa", CategoryIds = [category.Id], OrganizationId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(CatalogErrorCode.EquipmentType.OrganizationForbidden, problem.GetProperty("title").GetString());
        Assert.False(await dbContext.EquipmentTypes.AnyAsync(e => e.Code == "ORG-002"));
    }

    [Fact]
    public async Task Owner_MultipleOrganizations_Create_RequiresExplicitChoiceAmongOwnOrganizations()
    {
        Guid firstOrganization = Guid.NewGuid();
        Guid secondOrganization = Guid.NewGuid();
        Authenticate(OwnerToken(firstOrganization, secondOrganization));
        Category category = await CreateCategoryAsync("Org Multi", isActive: true);

        var ambiguous = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types",
            new CreateEquipmentTypeRequest { Code = "ORG-003", Name = "Loa", CategoryIds = [category.Id] });
        Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);
        var problem = await ambiguous.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(CatalogErrorCode.EquipmentType.OrganizationRequired, problem.GetProperty("title").GetString());

        var chosen = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types",
            new CreateEquipmentTypeRequest { Code = "ORG-003", Name = "Loa", CategoryIds = [category.Id], OrganizationId = secondOrganization });
        Assert.Equal(HttpStatusCode.Created, chosen.StatusCode);
        var detail = await chosen.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.Equal(secondOrganization, await ReadOrganizationIdAsync(detail!.Id));
    }

    [Fact]
    public async Task Owner_WithoutOrganizationClaim_Create_Returns403()
    {
        Authenticate(OwnerToken());
        Category category = await CreateCategoryAsync("Org None", isActive: true);

        var response = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types",
            new CreateEquipmentTypeRequest { Code = "ORG-004", Name = "Loa", CategoryIds = [category.Id] });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Owner_OwnOrganization_CanUpdatePublishAndDisable()
    {
        Guid organizationId = Guid.NewGuid();
        Guid id = await SeedEquipmentTypeAsync("ORG-OWN", organizationId);
        Authenticate(OwnerToken(organizationId));

        var patchRes = await client.PatchAsync($"/api/v1/catalog/equipment-types/{id}", PatchName("Tên mới"));
        Assert.Equal(HttpStatusCode.OK, patchRes.StatusCode);

        var publishRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publishRes.StatusCode);

        var disableRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{id}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disableRes.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Owner_CannotManage_RecordsOutsideOwnOrganization(bool ownedByOtherOrganization)
    {
        // false: bản ghi dùng chung (OrganizationId null) chỉ Admin quản lý.
        Guid id = await SeedEquipmentTypeAsync("ORG-OTHER", ownedByOtherOrganization ? Guid.NewGuid() : null);
        Authenticate(OwnerToken(Guid.NewGuid()));

        var patchRes = await client.PatchAsync($"/api/v1/catalog/equipment-types/{id}", PatchName("Chiếm quyền"));
        Assert.Equal(HttpStatusCode.Forbidden, patchRes.StatusCode);

        var publishRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{id}/publish", null);
        Assert.Equal(HttpStatusCode.Forbidden, publishRes.StatusCode);

        var disableRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{id}/disable", null);
        Assert.Equal(HttpStatusCode.Forbidden, disableRes.StatusCode);

        EquipmentType unchanged = await dbContext.EquipmentTypes.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Equal("Thiết bị ORG-OTHER", unchanged.Name);
        Assert.Equal(EquipmentTypeStatus.Draft, unchanged.Status);
    }

    [Fact]
    public async Task Admin_CanManage_RecordsOfAnyOrganization()
    {
        Guid id = await SeedEquipmentTypeAsync("ORG-ADMIN", Guid.NewGuid());
        Authenticate(AdminToken);

        var patchRes = await client.PatchAsync($"/api/v1/catalog/equipment-types/{id}", PatchName("Admin sửa"));
        Assert.Equal(HttpStatusCode.OK, patchRes.StatusCode);

        var publishRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publishRes.StatusCode);
    }

    [Theory]
    [InlineData("mem", null)]
    [InlineData("cus", null)]
    [InlineData("own", "svc")]
    public async Task EquipmentManage_RejectsNonOwnerRoles_AndServiceTokens(string role, string? tokenUse)
    {
        Guid organizationId = Guid.NewGuid();
        Guid id = await SeedEquipmentTypeAsync("ORG-ROLE", organizationId);
        Authenticate(TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: Guid.NewGuid().ToString(),
            role: role,
            tokenUse: tokenUse,
            organizationIds: [organizationId]));

        var patchRes = await client.PatchAsync($"/api/v1/catalog/equipment-types/{id}", PatchName("Không được"));

        Assert.Equal(HttpStatusCode.Forbidden, patchRes.StatusCode);
    }

    [Fact]
    public async Task IdentityHeaders_DoNotGrantOrganizationAccess()
    {
        Guid ownerOrganization = Guid.NewGuid();
        Guid id = await SeedEquipmentTypeAsync("ORG-HDR", ownerOrganization);
        Authenticate(OwnerToken(Guid.NewGuid()));

        using HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/catalog/equipment-types/{id}/publish");
        request.Headers.Add("X-Organization-Id", ownerOrganization.ToString());
        request.Headers.Add("X-Role", "adm");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

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
            categoryIds: [category.Id],
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
        Assert.Single(detail.Categories);
        Assert.Equal(category.Id, detail.Categories[0].Id);
        Assert.Single(detail.Media);
        Assert.Equal(mediaId, detail.Media[0].MediaId);
        Assert.NotNull(detail.Media[0].Url);
        Assert.Contains(mediaId.ToString("D"), detail.Media[0].Url);
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
                "categoryIds": ["{{category.Id}}"],
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
        var missingCatReq = new CreateEquipmentTypeRequest("EQ-MISSING", "Name", null, [Guid.NewGuid()], null);
        var missingRes = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", missingCatReq);
        Assert.Equal(HttpStatusCode.BadRequest, missingRes.StatusCode);

        // Inactive Category
        Category inactiveCat = await CreateCategoryAsync("Inactive Cat", isActive: false);
        var inactiveCatReq = new CreateEquipmentTypeRequest("EQ-INACTIVE", "Name", null, [inactiveCat.Id], null);
        var inactiveRes = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", inactiveCatReq);
        Assert.Equal(HttpStatusCode.BadRequest, inactiveRes.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateCode_Returns409Conflict()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Monitors", isActive: true);

        var req1 = new CreateEquipmentTypeRequest("MON-001", "Monitor A", null, [category.Id], null);
        var res1 = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", req1);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);

        var req2 = new CreateEquipmentTypeRequest("MON-001", "Monitor B", null, [category.Id], null);
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
            new StringContent("""{"categoryIds": null}""", Encoding.UTF8, "application/json"));
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
        Assert.Equal(2, detail1.Media.Count);

        // Xóa hết media bằng "media": []
        string patchEmptyMedia = """{"media": []}""";
        var res2 = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent(patchEmptyMedia, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var detail2 = await res2.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(detail2);
        Assert.Empty(detail2.Media);
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
                "categoryIds": ["{{category.Id}}"],
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
                "categoryIds": ["{{category.Id}}"],
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
                "categoryIds": ["{{category.Id}}"],
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
    public async Task Create_WithMoreThanMaxMediaCount_Returns400InvalidMedia()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Strings", isActive: true);
        string media = string.Join(
            ",",
            Enumerable.Range(0, 21).Select(index =>
                $$"""{ "mediaId": "{{Guid.NewGuid()}}", "displayOrder": {{index}}, "isThumbnail": false }"""));

        string json = $$"""
            {
                "code": "STR-021",
                "name": "Violin",
                "categoryIds": ["{{category.Id}}"],
                "media": [{{media}}]
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
                "categoryIds": ["{{category.Id}}"],
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
                "categoryIds": ["{{category.Id}}"],
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
                "categoryIds": ["{{category.Id}}"],
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
                "categoryIds": ["{{category.Id}}"],
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
                "categoryIds": ["{{category.Id}}"]
            }
            """;

        var response = await client.PostAsync(
            "/api/v1/catalog/equipment-types",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(detail);
        Assert.Empty(detail.Media);
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
        Assert.NotNull(detailDis.PublishedAt);

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

    [Fact]
    public async Task Create_WithMismatchedUploader_Returns403Forbidden()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Percussion", isActive: true);

        Guid mediaId = Guid.NewGuid();
        TestMediaClient.ForcedUploaderUserId = "different-user-999";

        var request = new CreateEquipmentTypeRequest(
            code: "PERC-001",
            name: "Percussion",
            description: "High quality",
            categoryIds: [category.Id],
            media: [new EquipmentTypeMediaItemDto(mediaId, 0, true)]
        );

        var response = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Đảm bảo không lưu vào database
        bool exists = await dbContext.EquipmentTypes.AnyAsync(e => e.Code == "PERC-001");
        Assert.False(exists);
    }

    [Fact]
    public async Task Create_WithMediaNotReady_Returns400BadRequest()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Violas", isActive: true);

        Guid mediaId = Guid.NewGuid();
        TestMediaClient.VerifyOverride = (id, ct) => Task.FromResult(Result.Success(new MediaVerificationDto(
            id, "PendingUpload", CurrentAdminUserId, "pic.jpg", "image/jpeg", 100, DateTimeOffset.UtcNow)));

        var request = new CreateEquipmentTypeRequest(
            code: "VLA-001",
            name: "Viola",
            description: null,
            categoryIds: [category.Id],
            media: [new EquipmentTypeMediaItemDto(mediaId, 0, true)]
        );

        var response = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        bool exists = await dbContext.EquipmentTypes.AnyAsync(e => e.Code == "VLA-001");
        Assert.False(exists);
    }

    [Fact]
    public async Task Create_WithInvalidMime_Returns400BadRequest()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Basses", isActive: true);

        Guid mediaId = Guid.NewGuid();
        TestMediaClient.VerifyOverride = (id, ct) => Task.FromResult(Result.Success(new MediaVerificationDto(
            id, "Ready", CurrentAdminUserId, "doc.pdf", "application/pdf", 100, DateTimeOffset.UtcNow)));

        var request = new CreateEquipmentTypeRequest(
            code: "BAS-001",
            name: "Bass",
            description: null,
            categoryIds: [category.Id],
            media: [new EquipmentTypeMediaItemDto(mediaId, 0, true)]
        );

        var response = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        bool exists = await dbContext.EquipmentTypes.AnyAsync(e => e.Code == "BAS-001");
        Assert.False(exists);
    }

    [Fact]
    public async Task Create_WhenMediaServiceUnavailable_Returns503()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Flutes 2", isActive: true);

        Guid mediaId = Guid.NewGuid();
        TestMediaClient.VerifyOverride = (id, ct) => Task.FromResult(Result.Failure<MediaVerificationDto>(
            Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "MediaService is down.")));

        var request = new CreateEquipmentTypeRequest(
            code: "FLU-002",
            name: "Flute 2",
            description: null,
            categoryIds: [category.Id],
            media: [new EquipmentTypeMediaItemDto(mediaId, 0, true)]
        );

        var response = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        bool exists = await dbContext.EquipmentTypes.AnyAsync(e => e.Code == "FLU-002");
        Assert.False(exists);
    }

    [Fact]
    public async Task Create_WhenVisibilityFails_Returns503_AndDoesNotSaveEquipment()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Organs", isActive: true);

        Guid mediaId = Guid.NewGuid();
        TestMediaClient.VisibilityOverride = (id, vis, ct) => Task.FromResult(Result.Failure(
            Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Visibility error.")));

        var request = new CreateEquipmentTypeRequest(
            code: "ORG-001",
            name: "Pipe Organ",
            description: null,
            categoryIds: [category.Id],
            media: [new EquipmentTypeMediaItemDto(mediaId, 0, true)]
        );

        var response = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        bool exists = await dbContext.EquipmentTypes.AnyAsync(e => e.Code == "ORG-001");
        Assert.False(exists);
    }

    [Fact]
    public async Task Patch_CategoryIds_ReplacesFullList_AndRejectsEmptyOrNull()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category cat1 = await CreateCategoryAsync("Cat 1", isActive: true);
        Category cat2 = await CreateCategoryAsync("Cat 2", isActive: true);
        Category cat3 = await CreateCategoryAsync("Cat 3", isActive: true);

        EquipmentType eq = await CreateEquipmentTypeAsync("MULT-001", "Multi Cat Item", cat1.Id);

        // Thay toàn bộ danh sách thành [cat2, cat3]
        string patchValid = $$"""
            {
                "categoryIds": ["{{cat2.Id}}", "{{cat3.Id}}"]
            }
            """;
        var res1 = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent(patchValid, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        var detail1 = await res1.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(detail1);
        Assert.Equal(2, detail1.Categories.Count);
        Assert.Contains(detail1.Categories, c => c.Id == cat2.Id);
        Assert.Contains(detail1.Categories, c => c.Id == cat3.Id);
        Assert.DoesNotContain(detail1.Categories, c => c.Id == cat1.Id);

        // Gửi [] bị từ chối 400
        string patchEmpty = """{"categoryIds": []}""";
        var resEmpty = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent(patchEmpty, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resEmpty.StatusCode);

        // Gửi null bị từ chối 400
        string patchNull = """{"categoryIds": null}""";
        var resNull = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent(patchNull, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resNull.StatusCode);
    }

    [Fact]
    public async Task Patch_CategoryIds_PreservesExistingInactiveCategory_AndRejectsNewInactiveCategory()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category existingCat = await CreateCategoryAsync("Old Cat", isActive: true);
        Category newActiveCat = await CreateCategoryAsync("New Active Cat", isActive: true);
        Category newInactiveCat = await CreateCategoryAsync("New Inactive Cat", isActive: false);

        EquipmentType eq = await CreateEquipmentTypeAsync("INACT-001", "Inactive test", existingCat.Id);

        // Biến existingCat thành inactive
        existingCat.IsActive = false;
        await dbContext.SaveChangesAsync();

        // PATCH giữ existingCat (đang inactive) và thêm newActiveCat -> HỢP LỆ!
        string patchKeepExistingInactive = $$"""
            {
                "categoryIds": ["{{existingCat.Id}}", "{{newActiveCat.Id}}"]
            }
            """;
        var resOk = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent(patchKeepExistingInactive, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resOk.StatusCode);

        // PATCH thêm newInactiveCat (chưa từng gắn) -> BỊ TỪ CHỐI 400!
        string patchNewInactive = $$"""
            {
                "categoryIds": ["{{existingCat.Id}}", "{{newInactiveCat.Id}}"]
            }
            """;
        var resReject = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}",
            new StringContent(patchNewInactive, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resReject.StatusCode);
    }

    [Fact]
    public async Task Category_CreateAndPatch_ImageMedia_AndUrlResolution()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);

        Guid imgId1 = Guid.NewGuid();
        var createCatReq = new CreateCategoryRequest("Guitars with image", "Description", imgId1);
        var createRes = await client.PostAsJsonAsync("/api/v1/catalog/categories", createCatReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

        var catDetail = await createRes.Content.ReadFromJsonAsync<CategoryDetailResponse>();
        Assert.NotNull(catDetail);
        Assert.Equal(imgId1, catDetail.ImageMediaId);
        Assert.NotNull(catDetail.ImageUrl);
        Assert.Contains(imgId1.ToString("D"), catDetail.ImageUrl);

        // PATCH gỡ ảnh bằng "imageMediaId": null
        string patchRemoveImg = """{"imageMediaId": null}""";
        var patchRes1 = await client.PatchAsync(
            $"/api/v1/catalog/categories/{catDetail.Id}",
            new StringContent(patchRemoveImg, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, patchRes1.StatusCode);
        var catDetailAfterRemove = await patchRes1.Content.ReadFromJsonAsync<CategoryDetailResponse>();
        Assert.NotNull(catDetailAfterRemove);
        Assert.Null(catDetailAfterRemove.ImageMediaId);
        Assert.Null(catDetailAfterRemove.ImageUrl);

        // PATCH gắn ảnh mới
        Guid imgId2 = Guid.NewGuid();
        string patchNewImg = $$"""{"imageMediaId": "{{imgId2}}"}""";
        var patchRes2 = await client.PatchAsync(
            $"/api/v1/catalog/categories/{catDetail.Id}",
            new StringContent(patchNewImg, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, patchRes2.StatusCode);
        var catDetailAfterNew = await patchRes2.Content.ReadFromJsonAsync<CategoryDetailResponse>();
        Assert.NotNull(catDetailAfterNew);
        Assert.Equal(imgId2, catDetailAfterNew.ImageMediaId);
        Assert.NotNull(catDetailAfterNew.ImageUrl);
        Assert.Contains(imgId2.ToString("D"), catDetailAfterNew.ImageUrl);

        // PATCH rỗng GUID bị từ chối
        string patchEmptyGuid = """{"imageMediaId": "00000000-0000-0000-0000-000000000000"}""";
        var patchResEmpty = await client.PatchAsync(
            $"/api/v1/catalog/categories/{catDetail.Id}",
            new StringContent(patchEmptyGuid, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, patchResEmpty.StatusCode);
    }

    [Fact]
    public async Task GetEndpoints_Succeed_WhenMediaServiceIsDown()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Public Get Cat", isActive: true);
        EquipmentType eq = await CreateEquipmentTypeAsync("PUB-001", "Public Item", category.Id, description: "Desc");
        eq.Publish(DateTime.UtcNow);
        await dbContext.SaveChangesAsync();

        // Cấu hình mock MediaService ném lỗi hoặc down
        TestMediaClient.VerifyOverride = (id, ct) => Task.FromResult(Result.Failure<MediaVerificationDto>(
            Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Service is down.")));

        // GET public listing vẫn thành công 200 OK mà không gọi MediaService
        using HttpClient anonymousClient = factory.CreateClient();
        var listRes = await anonymousClient.GetAsync("/api/v1/catalog/equipment-types");
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);

        // GET public detail vẫn thành công 200 OK
        var detailRes = await anonymousClient.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}");
        Assert.Equal(HttpStatusCode.OK, detailRes.StatusCode);
    }

    [Fact]
    public async Task Create_WhenApiKeyRejected_Returns503_AndDoesNotSaveCatalog()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("ApiKey Test Cat", isActive: true);

        Guid mediaId = Guid.NewGuid();
        TestMediaClient.VerifyOverride = (id, ct) => Task.FromResult(Result.Failure<MediaVerificationDto>(
            Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Khóa API nội bộ của MediaService bị từ chối.")));

        var request = new CreateEquipmentTypeRequest(
            code: "KEY-ERR-001",
            name: "Key Error Item",
            description: null,
            categoryIds: [category.Id],
            media: [new EquipmentTypeMediaItemDto(mediaId, 0, true)]
        );

        var response = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        bool exists = await dbContext.EquipmentTypes.AnyAsync(e => e.Code == "KEY-ERR-001");
        Assert.False(exists);
    }

    [Fact]
    public async Task Create_WhenTimeout_Returns503_AndDoesNotSaveCatalog()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("Timeout Test Cat", isActive: true);

        Guid mediaId = Guid.NewGuid();
        TestMediaClient.VerifyOverride = (id, ct) => Task.FromResult(Result.Failure<MediaVerificationDto>(
            Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Hết thời gian chờ phản hồi từ MediaService.")));

        var request = new CreateEquipmentTypeRequest(
            code: "TO-ERR-001",
            name: "Timeout Error Item",
            description: null,
            categoryIds: [category.Id],
            media: [new EquipmentTypeMediaItemDto(mediaId, 0, true)]
        );

        var response = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        bool exists = await dbContext.EquipmentTypes.AnyAsync(e => e.Code == "TO-ERR-001");
        Assert.False(exists);
    }

    [Fact]
    public async Task Create_WhenVisibilityFailsMidList_Returns503_AndDoesNotSaveCatalog()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("MidList Visibility Cat", isActive: true);

        Guid m1 = Guid.NewGuid();
        Guid m2 = Guid.NewGuid();

        // Verify succeeds for both, but Visibility fails on the 2nd
        TestMediaClient.VisibilityOverride = (id, vis, ct) =>
        {
            if (id == m2)
            {
                return Task.FromResult(Result.Failure(Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Visibility 2 failed")));
            }
            return Task.FromResult(Result.Success());
        };

        var request = new CreateEquipmentTypeRequest(
            code: "MID-ERR-001",
            name: "Mid Visibility Error Item",
            description: null,
            categoryIds: [category.Id],
            media: [
                new EquipmentTypeMediaItemDto(m1, 0, true),
                new EquipmentTypeMediaItemDto(m2, 1, false)
            ]
        );

        var response = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // Catalog changes MUST NOT be saved
        bool exists = await dbContext.EquipmentTypes.AnyAsync(e => e.Code == "MID-ERR-001");
        Assert.False(exists);
    }

    [Fact]
    public async Task Create_WhenDatabaseFailsAfterPublic_DoesNotRollbackMediaPublicStatus()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Category category = await CreateCategoryAsync("DB Fail Cat", isActive: true);

        // Pre-create an item with same code to trigger DB duplicate code failure
        await CreateEquipmentTypeAsync("DUP-DB-001", "Existing Item", category.Id);

        Guid mediaId = Guid.NewGuid();
        var publicCalls = new List<(Guid Id, string Vis)>();
        TestMediaClient.VisibilityOverride = (id, vis, ct) =>
        {
            publicCalls.Add((id, vis));
            return Task.FromResult(Result.Success());
        };

        var request = new CreateEquipmentTypeRequest(
            code: "DUP-DB-001",
            name: "Conflict Item",
            description: null,
            categoryIds: [category.Id],
            media: [new EquipmentTypeMediaItemDto(mediaId, 0, true)]
        );

        var response = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
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
        return eq;
    }

    public sealed class TestMediaServiceClient : IMediaServiceClient
    {
        public Func<Guid, CancellationToken, Task<Result<MediaVerificationDto>>>? VerifyOverride { get; set; }
        public Func<Guid, string, CancellationToken, Task<Result>>? VisibilityOverride { get; set; }
        public string? ForcedUploaderUserId { get; set; }

        public void Reset()
        {
            VerifyOverride = null;
            VisibilityOverride = null;
            ForcedUploaderUserId = null;
        }

        public async Task<Result<MediaVerificationDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            if (VerifyOverride != null)
            {
                return await VerifyOverride(mediaId, cancellationToken);
            }

            return Result.Success(new MediaVerificationDto(
                mediaId,
                "Ready",
                ForcedUploaderUserId ?? CurrentAdminUserId,
                "photo.jpg",
                "image/jpeg",
                1024,
                DateTimeOffset.UtcNow));
        }

        public async Task<Result> SetVisibilityAsync(Guid mediaId, string visibility, CancellationToken cancellationToken = default)
        {
            if (VisibilityOverride != null)
            {
                return await VisibilityOverride(mediaId, visibility, cancellationToken);
            }

            return Result.Success();
        }
    }
}
