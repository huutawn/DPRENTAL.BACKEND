using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CatalogService.Application.DTOs.Category;
using Dprental.CatalogService.Application.Clients;
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
public sealed class CategoryEndpointApiTests : IAsyncLifetime, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly CatalogServiceDbContext _dbContext;
    private readonly TestMediaClient _mediaClient = new();

    private static readonly Guid AdminUserGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public CategoryEndpointApiTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", PostgreSqlFixture.ConnectionString);
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseTestJwks();
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IMediaServiceClient>();
                    services.AddSingleton<IMediaServiceClient>(_mediaClient);
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
        await _dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE \"catalog\".\"Categories\" CASCADE;");
        _mediaClient.Reset();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _dbContext.Dispose();
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
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateAdminToken(orgIds));

    private void AuthenticateWithToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private void ClearAuthentication() =>
        _client.DefaultRequestHeaders.Authorization = null;

    // ========================================================
    // 1. Authorization Matrix
    // ========================================================

    [Fact]
    public async Task PublicEndpoints_AreAccessibleAnonymously()
    {
        ClearAuthentication();

        var listRes = await _client.GetAsync("/api/v1/catalog/categories");
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);

        var getRes = await _client.GetAsync($"/api/v1/catalog/categories/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, getRes.StatusCode);
    }

    [Theory]
    [InlineData("POST", "categories")]
    [InlineData("PATCH", "categories/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "categories/00000000-0000-0000-0000-000000000001/disable")]
    [InlineData("POST", "categories/00000000-0000-0000-0000-000000000001/enable")]
    [InlineData("GET", "admin/categories")]
    [InlineData("GET", "admin/categories/00000000-0000-0000-0000-000000000001")]
    public async Task AdminEndpoints_WithoutToken_Return401Unauthorized(string method, string relativePath)
    {
        ClearAuthentication();

        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/v1/catalog/{relativePath}");
        if (method is "POST" or "PATCH")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request);
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

        using var response = await _client.GetAsync("/api/v1/catalog/admin/categories");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoints_WithServiceToken_Return403Forbidden()
    {
        AuthenticateWithToken(GenerateServiceToken());

        using var response = await _client.GetAsync("/api/v1/catalog/admin/categories");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoints_WithAdmRole_Succeeds()
    {
        AuthenticateAsAdmin(Guid.NewGuid());

        var response = await _client.GetAsync("/api/v1/catalog/admin/categories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ========================================================
    // 2. Query Whitelisting & Validation
    // ========================================================

    [Fact]
    public async Task AdminSearch_RejectsDisallowedQueryKeys_With400()
    {
        AuthenticateAsAdmin();
        var response = await _client.GetAsync("/api/v1/catalog/admin/categories?unsupportedKey=123");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Category.InvalidQuery, problem?.Title);
    }

    [Theory]
    [InlineData("/api/v1/catalog/admin/categories?page=0", CatalogErrorCode.Paging.InvalidPage)]
    [InlineData("/api/v1/catalog/admin/categories?pageSize=0", CatalogErrorCode.Paging.InvalidPageSize)]
    [InlineData("/api/v1/catalog/admin/categories?pageSize=101", CatalogErrorCode.Paging.InvalidPageSize)]
    [InlineData("/api/v1/catalog/admin/categories?sort=unsupported", CatalogErrorCode.Category.InvalidSort)]
    [InlineData("/api/v1/catalog/admin/categories?keyword=%00", CatalogErrorCode.Category.InvalidKeyword)]
    public async Task AdminSearch_ValidatesPaginationAndSort_With400(string url, string expectedErrorCode)
    {
        AuthenticateAsAdmin();
        var response = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(expectedErrorCode, problem?.Title);
    }

    [Fact]
    public async Task AdminSearch_RejectsOffsetOverflow_With400()
    {
        AuthenticateAsAdmin();
        var response = await _client.GetAsync($"/api/v1/catalog/admin/categories?page={int.MaxValue}&pageSize=100");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Paging.Overflow, problem?.Title);
    }

    [Fact]
    public async Task AdminSearch_RejectsUnbindableIsActive_With400ProblemDetails()
    {
        AuthenticateAsAdmin();
        var response = await _client.GetAsync("/api/v1/catalog/admin/categories?isActive=not_a_boolean");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Catalog.InvalidRequest, problem?.Title);
    }

    [Fact]
    public async Task AdminSearch_AcceptsIsActiveFilter()
    {
        AuthenticateAsAdmin();
        var responseTrue = await _client.GetAsync("/api/v1/catalog/admin/categories?isActive=true");
        Assert.Equal(HttpStatusCode.OK, responseTrue.StatusCode);

        var responseFalse = await _client.GetAsync("/api/v1/catalog/admin/categories?isActive=false");
        Assert.Equal(HttpStatusCode.OK, responseFalse.StatusCode);
    }

    // ========================================================
    // 3. Empty GUID and NotFound Validation
    // ========================================================

    [Fact]
    public async Task EmptyGuid_Returns400BadRequest_ForAdminAndActionEndpoints()
    {
        AuthenticateAsAdmin();
        string emptyId = Guid.Empty.ToString();

        // Admin GetById
        var getRes = await _client.GetAsync($"/api/v1/catalog/admin/categories/{emptyId}");
        Assert.Equal(HttpStatusCode.BadRequest, getRes.StatusCode);
        var getProb = await getRes.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Category.InvalidId, getProb?.Title);

        // Disable
        var disRes = await _client.PostAsync($"/api/v1/catalog/categories/{emptyId}/disable", null);
        Assert.Equal(HttpStatusCode.BadRequest, disRes.StatusCode);
        var disProb = await disRes.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Category.InvalidId, disProb?.Title);

        // Enable
        var enRes = await _client.PostAsync($"/api/v1/catalog/categories/{emptyId}/enable", null);
        Assert.Equal(HttpStatusCode.BadRequest, enRes.StatusCode);
        var enProb = await enRes.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Category.InvalidId, enProb?.Title);

        // Patch
        var patchRes = await _client.PatchAsJsonAsync($"/api/v1/catalog/categories/{emptyId}", new { name = "Valid Name" });
        Assert.Equal(HttpStatusCode.BadRequest, patchRes.StatusCode);
        var patchProb = await patchRes.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Category.InvalidId, patchProb?.Title);
    }

    [Fact]
    public async Task NotFound_Returns404ProblemDetails_ForAdminAndActionEndpoints()
    {
        AuthenticateAsAdmin();
        Guid nonExistent = Guid.NewGuid();

        // Admin GetById
        var getRes = await _client.GetAsync($"/api/v1/catalog/admin/categories/{nonExistent}");
        Assert.Equal(HttpStatusCode.NotFound, getRes.StatusCode);
        var getProb = await getRes.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Category.NotFound, getProb?.Title);

        // Disable
        var disRes = await _client.PostAsync($"/api/v1/catalog/categories/{nonExistent}/disable", null);
        Assert.Equal(HttpStatusCode.NotFound, disRes.StatusCode);
        var disProb = await disRes.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Category.NotFound, disProb?.Title);

        // Enable
        var enRes = await _client.PostAsync($"/api/v1/catalog/categories/{nonExistent}/enable", null);
        Assert.Equal(HttpStatusCode.NotFound, enRes.StatusCode);
        var enProb = await enRes.Content.ReadFromJsonAsync<ProblemDetailsJson>();
        Assert.Equal(CatalogErrorCode.Category.NotFound, enProb?.Title);
    }

    // ========================================================
    // 4. Disable / Enable Lifecycle & Idempotency
    // ========================================================

    [Fact]
    public async Task DisableAndEnable_Lifecycle_PublicVisibility_AndIdempotency()
    {
        AuthenticateAsAdmin();

        // 1. Tạo Category
        var createRes = await _client.PostAsJsonAsync("/api/v1/catalog/categories", new
        {
            name = "Audio Recorders",
            description = "High quality sound recorders"
        });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var cat = await createRes.Content.ReadFromJsonAsync<CategoryDetailResponse>();
        Assert.NotNull(cat);
        Assert.True(cat.IsActive);

        // 2. Public đọc được khi đang active
        ClearAuthentication();
        var pubGetActive = await _client.GetAsync($"/api/v1/catalog/categories/{cat.Id}");
        Assert.Equal(HttpStatusCode.OK, pubGetActive.StatusCode);

        // 3. Disable lần 1
        AuthenticateAsAdmin();
        var dis1 = await _client.PostAsync($"/api/v1/catalog/categories/{cat.Id}/disable", null);
        Assert.Equal(HttpStatusCode.OK, dis1.StatusCode);
        var disCat1 = await dis1.Content.ReadFromJsonAsync<CategoryDetailResponse>();
        Assert.NotNull(disCat1);
        Assert.False(disCat1.IsActive);

        // Lấy timestamp từ DB để so sánh
        var entityAfterDis1 = await _dbContext.Categories.AsNoTracking().FirstAsync(c => c.Id == cat.Id);
        Assert.NotNull(entityAfterDis1.UpdatedAtUtc);
        DateTime disabledAt = entityAfterDis1.UpdatedAtUtc.Value;

        // 4. Public đọc trả về 404 vì đã inactive
        ClearAuthentication();
        var pubGetInactive = await _client.GetAsync($"/api/v1/catalog/categories/{cat.Id}");
        Assert.Equal(HttpStatusCode.NotFound, pubGetInactive.StatusCode);

        // 5. Admin đọc được cả inactive qua GET /admin/categories/{id}
        AuthenticateAsAdmin();
        var admGetInactive = await _client.GetAsync($"/api/v1/catalog/admin/categories/{cat.Id}");
        Assert.Equal(HttpStatusCode.OK, admGetInactive.StatusCode);
        var admCat = await admGetInactive.Content.ReadFromJsonAsync<CategoryDetailResponse>();
        Assert.False(admCat?.IsActive);

        // 6. Disable lần 2 (Idempotent): gọi lại không đổi timestamp
        var dis2 = await _client.PostAsync($"/api/v1/catalog/categories/{cat.Id}/disable", null);
        Assert.Equal(HttpStatusCode.OK, dis2.StatusCode);
        var entityAfterDis2 = await _dbContext.Categories.AsNoTracking().FirstAsync(c => c.Id == cat.Id);
        Assert.Equal(disabledAt, entityAfterDis2.UpdatedAtUtc);

        // 7. Enable lần 1
        var en1 = await _client.PostAsync($"/api/v1/catalog/categories/{cat.Id}/enable", null);
        Assert.Equal(HttpStatusCode.OK, en1.StatusCode);
        var enCat1 = await en1.Content.ReadFromJsonAsync<CategoryDetailResponse>();
        Assert.NotNull(enCat1);
        Assert.True(enCat1.IsActive);

        var entityAfterEn1 = await _dbContext.Categories.AsNoTracking().FirstAsync(c => c.Id == cat.Id);
        Assert.NotNull(entityAfterEn1.UpdatedAtUtc);
        DateTime enabledAt = entityAfterEn1.UpdatedAtUtc.Value;

        // 8. Enable lần 2 (Idempotent): gọi lại không đổi timestamp
        var en2 = await _client.PostAsync($"/api/v1/catalog/categories/{cat.Id}/enable", null);
        Assert.Equal(HttpStatusCode.OK, en2.StatusCode);
        var entityAfterEn2 = await _dbContext.Categories.AsNoTracking().FirstAsync(c => c.Id == cat.Id);
        Assert.Equal(enabledAt, entityAfterEn2.UpdatedAtUtc);

        // 9. Public đọc lại được
        ClearAuthentication();
        var pubGetReenabled = await _client.GetAsync($"/api/v1/catalog/categories/{cat.Id}");
        Assert.Equal(HttpStatusCode.OK, pubGetReenabled.StatusCode);
    }

    [Fact]
    public async Task Patch_IsActive_UsesSameTransitionRules_AndIsIdempotent()
    {
        AuthenticateAsAdmin();

        // 1. Tạo Category
        var createRes = await _client.PostAsJsonAsync("/api/v1/catalog/categories", new
        {
            name = "Studio Monitors",
            description = "Active studio monitors"
        });
        var cat = await createRes.Content.ReadFromJsonAsync<CategoryDetailResponse>();
        Assert.NotNull(cat);

        // 2. PATCH isActive: false
        var patch1 = await _client.PatchAsJsonAsync($"/api/v1/catalog/categories/{cat.Id}", new
        {
            isActive = false
        });
        Assert.Equal(HttpStatusCode.OK, patch1.StatusCode);
        var patched1 = await patch1.Content.ReadFromJsonAsync<CategoryDetailResponse>();
        Assert.False(patched1?.IsActive);

        var dbEntity1 = await _dbContext.Categories.AsNoTracking().FirstAsync(c => c.Id == cat.Id);
        Assert.NotNull(dbEntity1.UpdatedAtUtc);
        DateTime disabledAt = dbEntity1.UpdatedAtUtc.Value;

        // 3. PATCH isActive: false lần 2 khi đã inactive (Idempotent): không đổi timestamp
        var patch2 = await _client.PatchAsJsonAsync($"/api/v1/catalog/categories/{cat.Id}", new
        {
            isActive = false
        });
        Assert.Equal(HttpStatusCode.OK, patch2.StatusCode);
        var dbEntity2 = await _dbContext.Categories.AsNoTracking().FirstAsync(c => c.Id == cat.Id);
        Assert.Equal(disabledAt, dbEntity2.UpdatedAtUtc);

        // 4. PATCH isActive: true
        var patch3 = await _client.PatchAsJsonAsync($"/api/v1/catalog/categories/{cat.Id}", new
        {
            isActive = true
        });
        Assert.Equal(HttpStatusCode.OK, patch3.StatusCode);
        var patched3 = await patch3.Content.ReadFromJsonAsync<CategoryDetailResponse>();
        Assert.True(patched3?.IsActive);

        var dbEntity3 = await _dbContext.Categories.AsNoTracking().FirstAsync(c => c.Id == cat.Id);
        DateTime enabledAt = dbEntity3.UpdatedAtUtc!.Value;

        // 5. PATCH isActive: true lần 2 khi đã active (Idempotent): không đổi timestamp
        var patch4 = await _client.PatchAsJsonAsync($"/api/v1/catalog/categories/{cat.Id}", new
        {
            isActive = true
        });
        Assert.Equal(HttpStatusCode.OK, patch4.StatusCode);
        var dbEntity4 = await _dbContext.Categories.AsNoTracking().FirstAsync(c => c.Id == cat.Id);
        Assert.Equal(enabledAt, dbEntity4.UpdatedAtUtc);
    }

    [Fact]
    public async Task PublicList_RemainsUnpagedArrayOfActiveCategories()
    {
        AuthenticateAsAdmin();

        await _client.PostAsJsonAsync("/api/v1/catalog/categories", new { name = "Public Active A" });
        await _client.PostAsJsonAsync("/api/v1/catalog/categories", new { name = "Public Active B" });

        var resInactive = await _client.PostAsJsonAsync("/api/v1/catalog/categories", new { name = "Public Inactive C" });
        var inactiveCat = await resInactive.Content.ReadFromJsonAsync<CategoryDetailResponse>();
        await _client.PostAsync($"/api/v1/catalog/categories/{inactiveCat!.Id}/disable", null);

        // Public GET /categories: trả về mảng, chỉ chứa active, không có paging metadata wrapper
        ClearAuthentication();
        var publicRes = await _client.GetAsync("/api/v1/catalog/categories");
        Assert.Equal(HttpStatusCode.OK, publicRes.StatusCode);

        var items = await publicRes.Content.ReadFromJsonAsync<List<CategoryDetailResponse>>();
        Assert.NotNull(items);
        Assert.Equal(2, items.Count);
        Assert.All(items, c => Assert.True(c.IsActive));
        Assert.Contains(items, c => c.Name == "Public Active A");
        Assert.Contains(items, c => c.Name == "Public Active B");
        Assert.DoesNotContain(items, c => c.Name == "Public Inactive C");

        // Admin GET /admin/categories: trả về PagedResult có cả 3
        AuthenticateAsAdmin();
        var adminRes = await _client.GetAsync("/api/v1/catalog/admin/categories");
        Assert.Equal(HttpStatusCode.OK, adminRes.StatusCode);
        var pagedAdmin = await adminRes.Content.ReadFromJsonAsync<PagedResult<CategoryDetailResponse>>();
        Assert.NotNull(pagedAdmin);
        Assert.Equal(3, pagedAdmin.TotalCount);
        Assert.Equal(3, pagedAdmin.Items.Count);
    }

    private sealed class ProblemDetailsJson
    {
        public string? Title { get; set; }
        public string? Detail { get; set; }
        public int? Status { get; set; }
    }

    private sealed class TestMediaClient : IMediaServiceClient
    {
        private int _callCount;

        public void Reset()
        {
            _callCount = 0;
        }

        public Task<Result<Dprental.CatalogService.Application.DTOs.Media.MediaVerificationDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            _callCount++;
            return Task.FromResult(Result.Success(new Dprental.CatalogService.Application.DTOs.Media.MediaVerificationDto(
                mediaId, "Ready", AdminUserGuid.ToString(), "test.png", "image/png", 1024, DateTimeOffset.UtcNow)));
        }

        public Task<Result> SetVisibilityAsync(Guid mediaId, string visibility, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }
}
