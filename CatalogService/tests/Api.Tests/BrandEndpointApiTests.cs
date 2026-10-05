using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.CatalogService.Application.Clients;
using Dprental.CatalogService.Application.Common;
using Dprental.CatalogService.Application.DTOs.Brands;
using Dprental.CatalogService.Application.DTOs.Media;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
[Collection(PostgreSqlTestGroup.Name)]
public sealed class BrandEndpointApiTests : IAsyncLifetime, IDisposable
{
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;
    private readonly CatalogServiceDbContext dbContext;
    private readonly TestMediaClient mediaClient = new();

    private static readonly Guid AdminUserGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public BrandEndpointApiTests()
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
                    services.AddSingleton<IMediaServiceClient>(mediaClient);
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
        await dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE \"catalog\".\"Brands\" CASCADE;");
        mediaClient.Reset();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        dbContext.Dispose();
    }

    private static string GenerateAdminToken(params Guid[] orgIds) =>
        TestJwtHelper.GenerateToken(
            sub: AdminUserGuid.ToString(),
            sid: Guid.NewGuid().ToString(),
            role: "adm",
            organizationIds: orgIds.Length > 0 ? orgIds : null);

    private static string GenerateUserToken(string role) =>
        TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: Guid.NewGuid().ToString(),
            role: role);

    private static string GenerateServiceToken() =>
        TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: null,
            tokenUse: "svc");

    private void AuthenticateAsAdmin(params Guid[] orgIds) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateAdminToken(orgIds));

    private void AuthenticateWithToken(string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private void ClearAuthentication() =>
        client.DefaultRequestHeaders.Authorization = null;

    // ========================================================
    // 1. Authorization Matrix
    // ========================================================

    [Fact]
    public async Task PublicEndpoints_AreAccessibleAnonymously()
    {
        ClearAuthentication();

        var listRes = await client.GetAsync("/api/v1/catalog/brands");
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);

        var getRes = await client.GetAsync($"/api/v1/catalog/brands/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, getRes.StatusCode);
    }

    [Theory]
    [InlineData("POST", "brands")]
    [InlineData("PATCH", "brands/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "brands/00000000-0000-0000-0000-000000000001/disable")]
    [InlineData("POST", "brands/00000000-0000-0000-0000-000000000001/enable")]
    [InlineData("GET", "admin/brands")]
    [InlineData("GET", "admin/brands/00000000-0000-0000-0000-000000000001")]
    public async Task AdminEndpoints_WithoutToken_Return401Unauthorized(string method, string relativePath)
    {
        ClearAuthentication();

        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/v1/catalog/{relativePath}");
        if (method is "POST" or "PATCH")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("cus")]
    [InlineData("own")]
    [InlineData("adp")]
    [InlineData("mem")]
    [InlineData("Admin")]
    [InlineData("admin")]
    public async Task AdminEndpoints_WithNonAdmRole_Return403Forbidden(string role)
    {
        AuthenticateWithToken(GenerateUserToken(role));

        using var response = await client.GetAsync("/api/v1/catalog/admin/brands");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoints_WithServiceToken_Return403Forbidden()
    {
        AuthenticateWithToken(GenerateServiceToken());

        using var response = await client.GetAsync("/api/v1/catalog/admin/brands");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoints_WithAdmRoleAndOrgClaims_SucceedsWithoutOrgInterference()
    {
        // Admin token có chứa organization claim nhưng quản lý brand toàn platform không xét org
        AuthenticateAsAdmin(Guid.NewGuid(), Guid.NewGuid());

        var response = await client.GetAsync("/api/v1/catalog/admin/brands");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ========================================================
    // 2. Query Whitelisting & Validation
    // ========================================================

    [Theory]
    [InlineData("/api/v1/catalog/brands?includeInactive=true")]
    [InlineData("/api/v1/catalog/brands?isActive=false")]
    [InlineData("/api/v1/catalog/brands?extra=value")]
    public async Task PublicSearch_RejectsDisallowedQueryKeys_With400(string url)
    {
        ClearAuthentication();
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/catalog/brands?page=0", CatalogErrorCode.Paging.InvalidPage)]
    [InlineData("/api/v1/catalog/brands?pageSize=0", CatalogErrorCode.Paging.InvalidPageSize)]
    [InlineData("/api/v1/catalog/brands?pageSize=101", CatalogErrorCode.Paging.InvalidPageSize)]
    [InlineData("/api/v1/catalog/brands?sort=random", CatalogErrorCode.Brand.InvalidSort)]
    [InlineData("/api/v1/catalog/brands?keyword=%00", CatalogErrorCode.Brand.InvalidKeyword)]
    public async Task PublicSearch_ValidatesPaginationAndSort_With400(string url, string expectedErrorCode)
    {
        ClearAuthentication();
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(expectedErrorCode, problem?.Title);
    }

    [Fact]
    public async Task AdminSearch_RejectsDisallowedQueryKeys_With400()
    {
        AuthenticateAsAdmin();
        var response = await client.GetAsync("/api/v1/catalog/admin/brands?unsupportedKey=123");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminSearch_AcceptsIsActiveFilter()
    {
        AuthenticateAsAdmin();
        var response = await client.GetAsync("/api/v1/catalog/admin/brands?isActive=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var response2 = await client.GetAsync("/api/v1/catalog/admin/brands?isActive=false");
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
    }

    [Fact]
    public async Task AdminSearch_WithNulKeyword_Returns400BadRequest()
    {
        AuthenticateAsAdmin();
        var response = await client.GetAsync("/api/v1/catalog/admin/brands?keyword=%00");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Brand.InvalidKeyword, problem?.Title);
    }

    // ========================================================
    // 3. Create Brand
    // ========================================================

    [Fact]
    public async Task CreateBrand_ValidData_Returns201Created_AndPersists()
    {
        AuthenticateAsAdmin();

        var body = new
        {
            code = "  sony  ",
            name = "  Sony   Corporation  ",
            slug = "  SONY-CORP  "
        };

        var response = await client.PostAsJsonAsync("/api/v1/catalog/brands", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<BrandDto>();
        Assert.NotNull(dto);
        Assert.NotEqual(Guid.Empty, dto.Id);
        Assert.Equal("SONY", dto.Code);
        Assert.Equal("Sony Corporation", dto.Name);
        Assert.Equal("sony-corp", dto.Slug);
        Assert.True(dto.IsActive);
        Assert.Null(dto.LogoMediaId);
        Assert.Null(dto.LogoUrl);
        Assert.Null(dto.UpdatedAt);
    }

    [Fact]
    public async Task CreateBrand_DuplicateCode_Returns409Conflict()
    {
        AuthenticateAsAdmin();

        var body1 = new { code = "CANON", name = "Canon Inc.", slug = "canon-1" };
        var r1 = await client.PostAsJsonAsync("/api/v1/catalog/brands", body1);
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);

        var body2 = new { code = "canon", name = "Canon Another", slug = "canon-2" };
        var r2 = await client.PostAsJsonAsync("/api/v1/catalog/brands", body2);
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);

        var problem = await r2.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Brand.DuplicateCode, problem?.Title);
    }

    [Fact]
    public async Task CreateBrand_DuplicateSlug_Returns409Conflict()
    {
        AuthenticateAsAdmin();

        var body1 = new { code = "NIKON-1", name = "Nikon 1", slug = "nikon" };
        var r1 = await client.PostAsJsonAsync("/api/v1/catalog/brands", body1);
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);

        var body2 = new { code = "NIKON-2", name = "Nikon 2", slug = "NIKON" };
        var r2 = await client.PostAsJsonAsync("/api/v1/catalog/brands", body2);
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);

        var problem = await r2.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Brand.DuplicateSlug, problem?.Title);
    }

    [Theory]
    [InlineData("""{"code": "", "name": "Sony", "slug": "sony"}""")]
    [InlineData("""{"code": "SONY", "name": "", "slug": "sony"}""")]
    [InlineData("""{"code": "SONY", "name": "Sony", "slug": ""}""")]
    [InlineData("""{"code": "SONY", "name": "Sony", "slug": "invalid_slug"}""")]
    [InlineData("""{"code": "SONY", "name": "Sony", "slug": "-sony"}""")]
    [InlineData("""{"code": "SONY", "name": "Sony", "slug": "sony-"}""")]
    [InlineData("""{"code": "SONY", "name": "Sony", "slug": "sony--alpha"}""")]
    [InlineData("""{"code": "SONY", "name": "Sony", "slug": "sony", "logoMediaId": "00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"code": "SONY", "name": "Sony", "slug": "sony", "organizationId": "11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"code": "SONY", "name": "Sony", "slug": "sony", "isActive": true}""")]
    [InlineData("""{"code": "SONY\u0000", "name": "Sony", "slug": "sony"}""")]
    [InlineData("""{"code": "SONY", "name": "Sony\u0000", "slug": "sony"}""")]
    [InlineData("""{"code": "SONY", "name": "Sony", "slug": "sony\u0000"}""")]
    public async Task CreateBrand_InvalidPayload_Returns400BadRequest(string json)
    {
        AuthenticateAsAdmin();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/catalog/brands")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ========================================================
    // 4. Update Brand (PATCH)
    // ========================================================

    [Fact]
    public async Task PatchBrand_ValidPartialUpdate_Returns200OK()
    {
        AuthenticateAsAdmin();

        var createRes = await client.PostAsJsonAsync("/api/v1/catalog/brands", new
        {
            code = "FUJI",
            name = "Fujifilm",
            slug = "fujifilm"
        });
        var brand = await createRes.Content.ReadFromJsonAsync<BrandDto>();
        Assert.NotNull(brand);

        var patchBody = new { name = "Fujifilm Global" };
        var patchRes = await client.PatchAsJsonAsync($"/api/v1/catalog/brands/{brand.Id}", patchBody);
        Assert.Equal(HttpStatusCode.OK, patchRes.StatusCode);

        var updated = await patchRes.Content.ReadFromJsonAsync<BrandDto>();
        Assert.NotNull(updated);
        Assert.Equal("FUJI", updated.Code); // Code giữ nguyên
        Assert.Equal("Fujifilm Global", updated.Name); // Name đã đổi
        Assert.Equal("fujifilm", updated.Slug); // Slug giữ nguyên
        Assert.NotNull(updated.UpdatedAt);
    }

    [Theory]
    [InlineData("{}")] // Empty patch
    [InlineData("""{"organizationId": "11111111-1111-1111-1111-111111111111"}""")] // Extra field
    [InlineData("""{"isActive": false}""")] // Extra field
    [InlineData("""{"code": null}""")] // Code null
    [InlineData("""{"name": ""}""")] // Name empty
    [InlineData("""{"slug": "invalid_slug"}""")] // Slug invalid
    [InlineData("""{"logoMediaId": "00000000-0000-0000-0000-000000000000"}""")] // Empty GUID
    [InlineData("""{"code": "SONY\u0000"}""")]
    [InlineData("""{"name": "Sony\u0000"}""")]
    [InlineData("""{"slug": "sony\u0000"}""")]
    public async Task PatchBrand_InvalidPayload_Returns400BadRequest(string json)
    {
        AuthenticateAsAdmin();

        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/catalog/brands/{Guid.NewGuid()}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchBrand_NotFound_Returns404()
    {
        AuthenticateAsAdmin();

        var response = await client.PatchAsJsonAsync($"/api/v1/catalog/brands/{Guid.NewGuid()}", new
        {
            name = "Non-existent Brand"
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ========================================================
    // 5. Disable and Enable Idempotency
    // ========================================================

    [Fact]
    public async Task DisableAndEnable_Lifecycle_AndPublicVisibility()
    {
        AuthenticateAsAdmin();

        // 1. Tạo Brand
        var createRes = await client.PostAsJsonAsync("/api/v1/catalog/brands", new
        {
            code = "GOPRO",
            name = "GoPro Inc.",
            slug = "gopro"
        });
        var brand = await createRes.Content.ReadFromJsonAsync<BrandDto>();
        Assert.NotNull(brand);

        // Public đọc được khi đang active
        ClearAuthentication();
        var pubGetActive = await client.GetAsync($"/api/v1/catalog/brands/{brand.Id}");
        Assert.Equal(HttpStatusCode.OK, pubGetActive.StatusCode);

        // 2. Disable lần 1
        AuthenticateAsAdmin();
        var dis1 = await client.PostAsync($"/api/v1/catalog/brands/{brand.Id}/disable", null);
        Assert.Equal(HttpStatusCode.OK, dis1.StatusCode);
        var disDto1 = await dis1.Content.ReadFromJsonAsync<BrandDto>();
        Assert.NotNull(disDto1);
        Assert.False(disDto1.IsActive);
        Assert.NotNull(disDto1.UpdatedAt);
        DateTime disabledAt = disDto1.UpdatedAt.Value;

        // Public đọc trả về 404 vì đã inactive
        ClearAuthentication();
        var pubGetInactive = await client.GetAsync($"/api/v1/catalog/brands/{brand.Id}");
        Assert.Equal(HttpStatusCode.NotFound, pubGetInactive.StatusCode);

        // Admin đọc được cả inactive
        AuthenticateAsAdmin();
        var admGetInactive = await client.GetAsync($"/api/v1/catalog/admin/brands/{brand.Id}");
        Assert.Equal(HttpStatusCode.OK, admGetInactive.StatusCode);
        var admDto = await admGetInactive.Content.ReadFromJsonAsync<BrandDto>();
        Assert.False(admDto?.IsActive);

        // 3. Disable lần 2 (Idempotent): không đổi UpdatedAt
        var dis2 = await client.PostAsync($"/api/v1/catalog/brands/{brand.Id}/disable", null);
        Assert.Equal(HttpStatusCode.OK, dis2.StatusCode);
        var disDto2 = await dis2.Content.ReadFromJsonAsync<BrandDto>();
        Assert.True(Math.Abs((disDto2!.UpdatedAt!.Value - disabledAt).TotalMilliseconds) < 1);

        // 4. Enable lần 1
        var en1 = await client.PostAsync($"/api/v1/catalog/brands/{brand.Id}/enable", null);
        Assert.Equal(HttpStatusCode.OK, en1.StatusCode);
        var enDto1 = await en1.Content.ReadFromJsonAsync<BrandDto>();
        Assert.NotNull(enDto1);
        Assert.True(enDto1.IsActive);
        Assert.NotNull(enDto1.UpdatedAt);
        DateTime enabledAt = enDto1.UpdatedAt.Value;

        // 5. Enable lần 2 (Idempotent): không đổi UpdatedAt
        var en2 = await client.PostAsync($"/api/v1/catalog/brands/{brand.Id}/enable", null);
        Assert.Equal(HttpStatusCode.OK, en2.StatusCode);
        var enDto2 = await en2.Content.ReadFromJsonAsync<BrandDto>();
        Assert.True(Math.Abs((enDto2!.UpdatedAt!.Value - enabledAt).TotalMilliseconds) < 1);

        // Public đọc lại được
        ClearAuthentication();
        var pubGetReenabled = await client.GetAsync($"/api/v1/catalog/brands/{brand.Id}");
        Assert.Equal(HttpStatusCode.OK, pubGetReenabled.StatusCode);
    }

    // ========================================================
    // 6. Media Integration Flow
    // ========================================================

    [Fact]
    public async Task CreateBrand_WithValidMedia_CallsVerifyAndPublic_AndResolvesUrl()
    {
        AuthenticateAsAdmin();

        Guid mediaId = Guid.NewGuid();
        mediaClient.Setup(mediaId, status: "Ready", contentType: "image/png", uploader: AdminUserGuid.ToString());

        var response = await client.PostAsJsonAsync("/api/v1/catalog/brands", new
        {
            code = "DJI",
            name = "DJI Drones",
            slug = "dji",
            logoMediaId = mediaId
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var brand = await response.Content.ReadFromJsonAsync<BrandDto>();
        Assert.NotNull(brand);
        Assert.Equal(mediaId, brand.LogoMediaId);
        Assert.Contains(mediaId.ToString("D"), brand.LogoUrl);
        Assert.Equal("public", mediaClient.GetVisibility(mediaId));
    }

    [Fact]
    public async Task CreateBrand_WithMediaNotReady_FailsAndDoesNotPersist()
    {
        AuthenticateAsAdmin();

        Guid mediaId = Guid.NewGuid();
        mediaClient.Setup(mediaId, status: "Processing", contentType: "image/png", uploader: AdminUserGuid.ToString());

        var response = await client.PostAsJsonAsync("/api/v1/catalog/brands", new
        {
            code = "HASSELBLAD",
            name = "Hasselblad",
            slug = "hasselblad",
            logoMediaId = mediaId
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Media.NotReady, problem?.Title);

        // Xác nhận brand không bị lưu
        var exists = await dbContext.Brands.AnyAsync(b => b.Code == "HASSELBLAD");
        Assert.False(exists);
    }

    [Fact]
    public async Task CreateBrand_WithInvalidMime_Fails()
    {
        AuthenticateAsAdmin();

        Guid mediaId = Guid.NewGuid();
        mediaClient.Setup(mediaId, status: "Ready", contentType: "application/pdf", uploader: AdminUserGuid.ToString());

        var response = await client.PostAsJsonAsync("/api/v1/catalog/brands", new
        {
            code = "LEICA",
            name = "Leica Camera",
            slug = "leica",
            logoMediaId = mediaId
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Media.InvalidContentType, problem?.Title);
    }

    [Fact]
    public async Task CreateBrand_WithDifferentUploader_FailsForbidden()
    {
        AuthenticateAsAdmin();

        Guid mediaId = Guid.NewGuid();
        mediaClient.Setup(mediaId, status: "Ready", contentType: "image/jpeg", uploader: Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/v1/catalog/brands", new
        {
            code = "SIGMA",
            name = "Sigma Corporation",
            slug = "sigma",
            logoMediaId = mediaId
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Media.Forbidden, problem?.Title);
    }

    [Fact]
    public async Task CreateBrand_WithNulField_DoesNotCallMediaService_AndDoesNotWriteDb()
    {
        AuthenticateAsAdmin();

        Guid mediaId = Guid.NewGuid();
        mediaClient.Setup(mediaId, status: "Ready", contentType: "image/png", uploader: AdminUserGuid.ToString());

        var response = await client.PostAsJsonAsync("/api/v1/catalog/brands", new
        {
            code = "PENTAX\0",
            name = "Pentax",
            slug = "pentax",
            logoMediaId = mediaId
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, mediaClient.VerifyCallCount);

        var exists = await dbContext.Brands.AnyAsync(b => b.Code.StartsWith("PENTAX"));
        Assert.False(exists);
    }

    [Fact]
    public async Task PatchBrand_RemovesLogo_WhenLogoMediaIdIsNull()
    {
        AuthenticateAsAdmin();

        Guid mediaId = Guid.NewGuid();
        mediaClient.Setup(mediaId, status: "Ready", contentType: "image/png", uploader: AdminUserGuid.ToString());

        var createRes = await client.PostAsJsonAsync("/api/v1/catalog/brands", new
        {
            code = "TAMRON",
            name = "Tamron",
            slug = "tamron",
            logoMediaId = mediaId
        });
        var brand = await createRes.Content.ReadFromJsonAsync<BrandDto>();
        Assert.NotNull(brand);
        Assert.NotNull(brand.LogoMediaId);

        // Gỡ logo: {"logoMediaId": null}
        int verifyCountBefore = mediaClient.VerifyCallCount;
        var patchRes = await client.PatchAsJsonAsync($"/api/v1/catalog/brands/{brand.Id}", new
        {
            logoMediaId = (Guid?)null
        });

        Assert.Equal(HttpStatusCode.OK, patchRes.StatusCode);
        var updated = await patchRes.Content.ReadFromJsonAsync<BrandDto>();
        Assert.NotNull(updated);
        Assert.Null(updated.LogoMediaId);
        Assert.Null(updated.LogoUrl);

        // Không gọi verify lại khi gỡ logo
        Assert.Equal(verifyCountBefore, mediaClient.VerifyCallCount);
    }

    [Fact]
    public async Task GetBrand_ResolvesUrlWithoutCallingMediaService()
    {
        AuthenticateAsAdmin();

        Guid mediaId = Guid.NewGuid();
        mediaClient.Setup(mediaId, status: "Ready", contentType: "image/png", uploader: AdminUserGuid.ToString());

        var createRes = await client.PostAsJsonAsync("/api/v1/catalog/brands", new
        {
            code = "RODE",
            name = "Rode Microphones",
            slug = "rode",
            logoMediaId = mediaId
        });
        var brand = await createRes.Content.ReadFromJsonAsync<BrandDto>();
        Assert.NotNull(brand);

        int verifyCallsBefore = mediaClient.VerifyCallCount;

        ClearAuthentication();
        var getRes = await client.GetAsync($"/api/v1/catalog/brands/{brand.Id}");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        var fetched = await getRes.Content.ReadFromJsonAsync<BrandDto>();
        Assert.NotNull(fetched?.LogoUrl);

        // Không có thêm cuộc gọi verify nào
        Assert.Equal(verifyCallsBefore, mediaClient.VerifyCallCount);
    }

    // ========================================================
    // Helpers & Test Doubles
    // ========================================================

    private sealed class ProblemDetailsJson
    {
        public string? Title { get; set; }
        public string? Detail { get; set; }
        public int? Status { get; set; }
    }

    private sealed class TestMediaClient : IMediaServiceClient
    {
        private readonly Dictionary<Guid, (string Status, string ContentType, string Uploader)> mediaItems = [];
        private readonly Dictionary<Guid, string> visibilityMap = [];

        public int VerifyCallCount { get; private set; }

        public void Reset()
        {
            mediaItems.Clear();
            visibilityMap.Clear();
            VerifyCallCount = 0;
        }

        public void Setup(Guid id, string status, string contentType, string uploader)
        {
            mediaItems[id] = (status, contentType, uploader);
        }

        public string? GetVisibility(Guid id) => visibilityMap.GetValueOrDefault(id);

        public Task<Result<MediaVerificationDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            VerifyCallCount++;
            if (!mediaItems.TryGetValue(mediaId, out var item))
            {
                return Task.FromResult(Result.Failure<MediaVerificationDto>(CatalogErrors.Media.NotFound));
            }

            return Task.FromResult(Result.Success(new MediaVerificationDto(
                mediaId, item.Status, item.Uploader, "logo.png", item.ContentType, 2048, DateTimeOffset.UtcNow)));
        }

        public Task<Result> SetVisibilityAsync(Guid mediaId, string visibility, CancellationToken cancellationToken = default)
        {
            visibilityMap[mediaId] = visibility;
            return Task.FromResult(Result.Success());
        }
    }
}
