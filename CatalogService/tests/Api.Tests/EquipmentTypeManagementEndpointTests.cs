using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.CatalogService.Application.DTOs.EquipmentType;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class EquipmentTypeManagementEndpointTests
{
    private const string BaseUrl = "/api/v1/catalog/equipment-types";

    [Fact]
    public async Task Create_ByAdmin_ReturnsDraft_AndIgnoresSupplierFields()
    {
        using EquipmentTypeApiFactory factory = new();
        Category category = await factory.SeedCategoryAsync();
        using HttpClient admin = factory.CreateAdminClient();

        // Các field của Supplier Asset và các field được bảo vệ đều phải bị bỏ qua.
        using HttpResponseMessage response = await admin.PostAsJsonAsync(BaseUrl, new
        {
            categoryId = category.Id,
            code = " forklift-1.5-3.5 ",
            name = "  Forklift 1.5–3.5T ",
            description = "Xe nâng tải trọng 1.5 đến 3.5 tấn",
            organizationId = Guid.NewGuid(),
            dailyPrice = 1500000,
            regionId = Guid.NewGuid(),
            availability = 3,
            specifications = new { fuel = "Diesel" },
            status = "Published",
            publishedAtUtc = DateTime.UtcNow
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        EquipmentTypeManagementResponse created = await ReadAsync(response);
        Assert.Equal(nameof(EquipmentTypeStatus.Draft), created.Status);
        Assert.Equal("FORKLIFT-1.5-3.5", created.Code);
        Assert.Equal("Forklift 1.5–3.5T", created.Name);
        Assert.Equal(category.Id, created.CategoryId);
        Assert.False(created.RequiresSpecSchema);
        Assert.False(created.IsCodeLocked);
        Assert.Null(created.PublishedAtUtc);
        Assert.EndsWith($"{BaseUrl}/{created.Id}/management", response.Headers.Location!.AbsolutePath, StringComparison.Ordinal);

        string json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("organizationId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dailyPrice", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("regionId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("availability", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManagementEndpoints_WithoutToken_Return401()
    {
        using EquipmentTypeApiFactory factory = new();
        using HttpClient anonymous = factory.CreateClient();
        Guid id = Guid.NewGuid();

        using HttpResponseMessage create = await anonymous.PostAsJsonAsync(BaseUrl, ValidRequest(Guid.NewGuid()));
        using HttpResponseMessage update = await anonymous.PatchAsJsonAsync($"{BaseUrl}/{id}", new { name = "x" });
        using HttpResponseMessage detail = await anonymous.GetAsync($"{BaseUrl}/{id}/management");
        using HttpResponseMessage publish = await anonymous.PostAsync($"{BaseUrl}/{id}/publish", null);
        using HttpResponseMessage disable = await anonymous.PostAsync($"{BaseUrl}/{id}/disable", null);

        Assert.All(
            new[] { create, update, detail, publish, disable },
            response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    [Theory]
    [InlineData("cus", null)]
    [InlineData("own", null)]
    [InlineData("Admin", null)]
    [InlineData(null, null)]
    [InlineData("adm", "svc")]
    [InlineData("adm", "other")]
    public async Task ManagementEndpoints_ForNonAdmin_Return403_AndChangeNothing(string? role, string? tokenUse)
    {
        using EquipmentTypeApiFactory factory = new();
        Category category = await factory.SeedCategoryAsync();
        using HttpClient admin = factory.CreateAdminClient();
        EquipmentTypeManagementResponse existing = await CreateAsync(admin, category.Id);
        using HttpClient nonAdmin = factory.CreateClientWithToken(role, tokenUse);

        using HttpResponseMessage create = await nonAdmin.PostAsJsonAsync(BaseUrl, ValidRequest(category.Id, code: "OTHER-1", name: "Other"));
        using HttpResponseMessage update = await nonAdmin.PatchAsJsonAsync($"{BaseUrl}/{existing.Id}", new { name = "Hacked" });
        using HttpResponseMessage detail = await nonAdmin.GetAsync($"{BaseUrl}/{existing.Id}/management");
        using HttpResponseMessage publish = await nonAdmin.PostAsync($"{BaseUrl}/{existing.Id}/publish", null);
        using HttpResponseMessage disable = await nonAdmin.PostAsync($"{BaseUrl}/{existing.Id}/disable", null);

        Assert.All(
            new[] { create, update, detail, publish, disable },
            response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        ProblemDetails forbidden = (await create.Content.ReadFromJsonAsync<ProblemDetails>(JsonSerializerOptions.Web))!;
        Assert.Equal(403, forbidden.Status);
        Assert.Equal("Forbidden", forbidden.Title);

        EquipmentTypeManagementResponse current = await GetAsync(admin, existing.Id);
        Assert.Equal(existing.Name, current.Name);
        Assert.Equal(nameof(EquipmentTypeStatus.Draft), current.Status);
        await factory.WithDbContextAsync(async db => Assert.Equal(1, await db.EquipmentTypes.CountAsync()));
    }

    [Fact]
    public async Task AdminPlatformRole_CanManage()
    {
        using EquipmentTypeApiFactory factory = new();
        Category category = await factory.SeedCategoryAsync();
        using HttpClient adminPlatform = factory.CreateAdminClient(role: "adp");

        using HttpResponseMessage response = await adminPlatform.PostAsJsonAsync(BaseUrl, ValidRequest(category.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithInvalidInput_Returns400ProblemDetails()
    {
        using EquipmentTypeApiFactory factory = new();
        Category active = await factory.SeedCategoryAsync();
        Category inactive = await factory.SeedCategoryAsync(name: "Ngừng", isActive: false);
        using HttpClient admin = factory.CreateAdminClient();

        using HttpResponseMessage missingName = await admin.PostAsJsonAsync(BaseUrl, new { categoryId = active.Id, code = "X-1" });
        Assert.Equal(HttpStatusCode.BadRequest, missingName.StatusCode);

        using HttpResponseMessage invalidCode = await admin.PostAsJsonAsync(BaseUrl, ValidRequest(active.Id, code: "xe nâng #1"));
        await AssertProblemAsync(invalidCode, HttpStatusCode.BadRequest, CatalogErrorCode.EquipmentType.CodeInvalid);

        using HttpResponseMessage unknownCategory = await admin.PostAsJsonAsync(BaseUrl, ValidRequest(Guid.NewGuid()));
        await AssertProblemAsync(unknownCategory, HttpStatusCode.BadRequest, CatalogErrorCode.Category.NotFound);

        using HttpResponseMessage inactiveCategory = await admin.PostAsJsonAsync(BaseUrl, ValidRequest(inactive.Id));
        await AssertProblemAsync(inactiveCategory, HttpStatusCode.BadRequest, CatalogErrorCode.Category.Inactive);
    }

    [Fact]
    public async Task Create_DuplicateCanonicalType_IsRejected()
    {
        using EquipmentTypeApiFactory factory = new();
        Category forklifts = await factory.SeedCategoryAsync();
        Category excavators = await factory.SeedCategoryAsync(name: "Máy xúc");
        using HttpClient admin = factory.CreateAdminClient();
        await CreateAsync(admin, forklifts.Id, code: "FORKLIFT-1.5-3.5", name: "Forklift 1.5–3.5T");

        // Trùng mã: mã là duy nhất toàn hệ thống, kể cả khác category và khác hoa thường.
        using HttpResponseMessage sameCode = await admin.PostAsJsonAsync(
            BaseUrl,
            ValidRequest(excavators.Id, code: "forklift-1.5-3.5", name: "Tên khác"));
        await AssertProblemAsync(sameCode, HttpStatusCode.Conflict, CatalogErrorCode.EquipmentType.CodeAlreadyExists);

        // Trùng tên trong cùng category, khác hoa thường và khoảng trắng.
        using HttpResponseMessage sameName = await admin.PostAsJsonAsync(
            BaseUrl,
            ValidRequest(forklifts.Id, code: "FORKLIFT-B", name: "forklift   1.5–3.5t"));
        await AssertProblemAsync(sameName, HttpStatusCode.Conflict, CatalogErrorCode.EquipmentType.NameAlreadyExists);

        // Cùng tên nhưng khác category thì không phải trùng.
        using HttpResponseMessage sameNameOtherCategory = await admin.PostAsJsonAsync(
            BaseUrl,
            ValidRequest(excavators.Id, code: "FORKLIFT-C", name: "Forklift 1.5–3.5T"));
        Assert.Equal(HttpStatusCode.Created, sameNameOtherCategory.StatusCode);
    }

    [Fact]
    public async Task Update_ChangesEditableFields_AndIgnoresProtectedOnes()
    {
        using EquipmentTypeApiFactory factory = new();
        Category category = await factory.SeedCategoryAsync();
        Category other = await factory.SeedCategoryAsync(name: "Máy xúc");
        using HttpClient admin = factory.CreateAdminClient();
        EquipmentTypeManagementResponse created = await CreateAsync(admin, category.Id);

        using HttpResponseMessage response = await admin.PatchAsJsonAsync($"{BaseUrl}/{created.Id}", new
        {
            categoryId = other.Id,
            code = "forklift-2",
            name = "Forklift 2T",
            description = "",
            status = "Published",
            publishedAtUtc = DateTime.UtcNow,
            createdAtUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        EquipmentTypeManagementResponse updated = await ReadAsync(response);
        Assert.Equal(other.Id, updated.CategoryId);
        Assert.Equal("FORKLIFT-2", updated.Code);
        Assert.Equal("Forklift 2T", updated.Name);
        Assert.Null(updated.Description);
        Assert.NotNull(updated.UpdatedAtUtc);

        Assert.Equal(nameof(EquipmentTypeStatus.Draft), updated.Status);
        Assert.Null(updated.PublishedAtUtc);
        Assert.Equal(created.CreatedAtUtc, updated.CreatedAtUtc);
    }

    [Fact]
    public async Task Update_ToDuplicateOrInvalidValues_IsRejected_AndChangesNothing()
    {
        using EquipmentTypeApiFactory factory = new();
        Category category = await factory.SeedCategoryAsync();
        Category inactive = await factory.SeedCategoryAsync(name: "Ngừng", isActive: false);
        using HttpClient admin = factory.CreateAdminClient();
        await CreateAsync(admin, category.Id, code: "A-1", name: "Type A");
        EquipmentTypeManagementResponse second = await CreateAsync(admin, category.Id, code: "A-2", name: "Type B");

        using HttpResponseMessage duplicateCode = await admin.PatchAsJsonAsync($"{BaseUrl}/{second.Id}", new { code = "a-1" });
        await AssertProblemAsync(duplicateCode, HttpStatusCode.Conflict, CatalogErrorCode.EquipmentType.CodeAlreadyExists);

        using HttpResponseMessage duplicateName = await admin.PatchAsJsonAsync($"{BaseUrl}/{second.Id}", new { name = "type  a" });
        await AssertProblemAsync(duplicateName, HttpStatusCode.Conflict, CatalogErrorCode.EquipmentType.NameAlreadyExists);

        using HttpResponseMessage blankName = await admin.PatchAsJsonAsync($"{BaseUrl}/{second.Id}", new { name = "   " });
        await AssertProblemAsync(blankName, HttpStatusCode.BadRequest, CatalogErrorCode.EquipmentType.NameRequired);

        using HttpResponseMessage inactiveCategory = await admin.PatchAsJsonAsync($"{BaseUrl}/{second.Id}", new { categoryId = inactive.Id });
        await AssertProblemAsync(inactiveCategory, HttpStatusCode.BadRequest, CatalogErrorCode.Category.Inactive);

        using HttpResponseMessage unknown = await admin.PatchAsJsonAsync($"{BaseUrl}/{Guid.NewGuid()}", new { name = "x" });
        await AssertProblemAsync(unknown, HttpStatusCode.NotFound, CatalogErrorCode.EquipmentType.NotFound);

        EquipmentTypeManagementResponse current = await GetAsync(admin, second.Id);
        Assert.Equal("A-2", current.Code);
        Assert.Equal("Type B", current.Name);
        Assert.Equal(category.Id, current.CategoryId);

        // Gửi lại đúng giá trị hiện tại không bị coi là trùng với chính nó.
        using HttpResponseMessage sameValues = await admin.PatchAsJsonAsync($"{BaseUrl}/{second.Id}", new { code = "a-2", name = "Type B" });
        Assert.Equal(HttpStatusCode.OK, sameValues.StatusCode);
    }

    [Fact]
    public async Task Lifecycle_DraftPublishedDisabledPublished_ControlsPublicVisibility()
    {
        using EquipmentTypeApiFactory factory = new();
        Category category = await factory.SeedCategoryAsync();
        using HttpClient admin = factory.CreateAdminClient();
        using HttpClient anonymous = factory.CreateClient();
        EquipmentTypeManagementResponse created = await CreateAsync(admin, category.Id);

        // Draft: không public, không disable được.
        Assert.Empty(await ListPublicAsync(anonymous));
        using HttpResponseMessage draftDetail = await anonymous.GetAsync($"{BaseUrl}/{created.Id}");
        await AssertProblemAsync(draftDetail, HttpStatusCode.NotFound, CatalogErrorCode.EquipmentType.NotFound);
        using HttpResponseMessage disableDraft = await admin.PostAsync($"{BaseUrl}/{created.Id}/disable", null);
        await AssertProblemAsync(disableDraft, HttpStatusCode.Conflict, CatalogErrorCode.EquipmentType.NotPublished);

        // Published: public.
        using HttpResponseMessage publish = await admin.PostAsync($"{BaseUrl}/{created.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        EquipmentTypeManagementResponse published = await ReadAsync(publish);
        Assert.Equal(nameof(EquipmentTypeStatus.Published), published.Status);
        Assert.NotNull(published.PublishedAtUtc);
        Assert.True(published.IsCodeLocked);

        EquipmentTypeResponse publicItem = Assert.Single(await ListPublicAsync(anonymous));
        Assert.Equal(created.Id, publicItem.Id);
        Assert.Equal(category.Name, publicItem.CategoryName);
        using HttpResponseMessage publicDetail = await anonymous.GetAsync($"{BaseUrl}/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, publicDetail.StatusCode);

        using HttpResponseMessage publishAgain = await admin.PostAsync($"{BaseUrl}/{created.Id}/publish", null);
        await AssertProblemAsync(publishAgain, HttpStatusCode.Conflict, CatalogErrorCode.EquipmentType.AlreadyPublished);

        // Disabled: không public nữa, dữ liệu vẫn còn cho Admin.
        using HttpResponseMessage disable = await admin.PostAsync($"{BaseUrl}/{created.Id}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        EquipmentTypeManagementResponse disabled = await ReadAsync(disable);
        Assert.Equal(nameof(EquipmentTypeStatus.Disabled), disabled.Status);
        Assert.NotNull(disabled.DisabledAtUtc);

        Assert.Empty(await ListPublicAsync(anonymous));
        using HttpResponseMessage disabledDetail = await anonymous.GetAsync($"{BaseUrl}/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, disabledDetail.StatusCode);
        Assert.Equal(nameof(EquipmentTypeStatus.Disabled), (await GetAsync(admin, created.Id)).Status);

        using HttpResponseMessage disableAgain = await admin.PostAsync($"{BaseUrl}/{created.Id}/disable", null);
        await AssertProblemAsync(disableAgain, HttpStatusCode.Conflict, CatalogErrorCode.EquipmentType.NotPublished);

        // Publish lại.
        using HttpResponseMessage republish = await admin.PostAsync($"{BaseUrl}/{created.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, republish.StatusCode);
        EquipmentTypeManagementResponse republished = await ReadAsync(republish);
        Assert.Equal(nameof(EquipmentTypeStatus.Published), republished.Status);
        Assert.Null(republished.DisabledAtUtc);
        Assert.Single(await ListPublicAsync(anonymous));
    }

    [Fact]
    public async Task Publish_WhenCategoryWasDeactivated_Returns422()
    {
        using EquipmentTypeApiFactory factory = new();
        Category category = await factory.SeedCategoryAsync();
        using HttpClient admin = factory.CreateAdminClient();
        EquipmentTypeManagementResponse created = await CreateAsync(admin, category.Id);
        await factory.WithDbContextAsync(async db =>
        {
            Category stored = await db.Categories.SingleAsync(c => c.Id == category.Id);
            stored.IsActive = false;
            await db.SaveChangesAsync();
        });

        using HttpResponseMessage response = await admin.PostAsync($"{BaseUrl}/{created.Id}/publish", null);

        ProblemDetails problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, CatalogErrorCode.EquipmentType.NotPublishable);
        Assert.Contains("category", problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(nameof(EquipmentTypeStatus.Draft), (await GetAsync(admin, created.Id)).Status);
    }

    [Fact]
    public async Task Publish_TypeRequiringSpecSchema_NeedsPublishedSchemaVersion()
    {
        FakeSpecSchemaCatalog specSchemas = new();
        using EquipmentTypeApiFactory baseFactory = new();
        using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISpecSchemaCatalog>();
                services.AddSingleton<ISpecSchemaCatalog>(specSchemas);
            }));
        Category category = await baseFactory.SeedCategoryAsync();
        using HttpClient admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            TestJwtHelper.GenerateToken(sub: Guid.NewGuid().ToString(), sid: Guid.NewGuid().ToString(), role: "adm"));

        using HttpResponseMessage create = await admin.PostAsJsonAsync(BaseUrl, new
        {
            categoryId = category.Id,
            code = "CRANE-25T",
            name = "Crane 25T",
            requiresSpecSchema = true
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        EquipmentTypeManagementResponse created = await ReadAsync(create);
        Assert.True(created.RequiresSpecSchema);

        using HttpResponseMessage withoutSchema = await admin.PostAsync($"{BaseUrl}/{created.Id}/publish", null);
        ProblemDetails problem = await AssertProblemAsync(withoutSchema, HttpStatusCode.UnprocessableEntity, CatalogErrorCode.EquipmentType.NotPublishable);
        Assert.Contains("SpecSchema", problem.Detail, StringComparison.Ordinal);

        specSchemas.PublishedFor.Add(created.Id);
        using HttpResponseMessage withSchema = await admin.PostAsync($"{BaseUrl}/{created.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, withSchema.StatusCode);
    }

    [Fact]
    public async Task Publish_WithDefaultSpecSchemaCatalog_BlocksTypesThatRequireSchema()
    {
        using EquipmentTypeApiFactory factory = new();
        Category category = await factory.SeedCategoryAsync();
        using HttpClient admin = factory.CreateAdminClient();
        using HttpResponseMessage create = await admin.PostAsJsonAsync(BaseUrl, new
        {
            categoryId = category.Id,
            code = "CRANE-50T",
            name = "Crane 50T",
            requiresSpecSchema = true
        });
        EquipmentTypeManagementResponse created = await ReadAsync(create);

        using HttpResponseMessage publish = await admin.PostAsync($"{BaseUrl}/{created.Id}/publish", null);
        await AssertProblemAsync(publish, HttpStatusCode.UnprocessableEntity, CatalogErrorCode.EquipmentType.NotPublishable);

        // Tắt yêu cầu spec thì publish được.
        using HttpResponseMessage relax = await admin.PatchAsJsonAsync($"{BaseUrl}/{created.Id}", new { requiresSpecSchema = false });
        Assert.Equal(HttpStatusCode.OK, relax.StatusCode);
        using HttpResponseMessage publishAgain = await admin.PostAsync($"{BaseUrl}/{created.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publishAgain.StatusCode);
    }

    [Fact]
    public async Task Update_AfterPublish_LocksCode_AndKeepsTypePublishable()
    {
        using EquipmentTypeApiFactory factory = new();
        Category category = await factory.SeedCategoryAsync();
        using HttpClient admin = factory.CreateAdminClient();
        EquipmentTypeManagementResponse created = await CreateAsync(admin, category.Id, code: "LOCK-1");
        using HttpResponseMessage publish = await admin.PostAsync($"{BaseUrl}/{created.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        using HttpResponseMessage changeCode = await admin.PatchAsJsonAsync($"{BaseUrl}/{created.Id}", new { code = "LOCK-2" });
        await AssertProblemAsync(changeCode, HttpStatusCode.Conflict, CatalogErrorCode.EquipmentType.CodeLocked);

        using HttpResponseMessage rename = await admin.PatchAsJsonAsync($"{BaseUrl}/{created.Id}", new { code = "lock-1", name = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        Assert.Equal(nameof(EquipmentTypeStatus.Published), (await ReadAsync(rename)).Status);

        // Type đang công khai không được bật yêu cầu spec khi chưa có schema.
        using HttpResponseMessage requireSpec = await admin.PatchAsJsonAsync($"{BaseUrl}/{created.Id}", new { requiresSpecSchema = true });
        await AssertProblemAsync(requireSpec, HttpStatusCode.UnprocessableEntity, CatalogErrorCode.EquipmentType.NotPublishable);
        Assert.False((await GetAsync(admin, created.Id)).RequiresSpecSchema);

        // Mã vẫn bị khóa sau khi disable, vì có thể đã được tham chiếu.
        using HttpResponseMessage disable = await admin.PostAsync($"{BaseUrl}/{created.Id}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        using HttpResponseMessage changeCodeDisabled = await admin.PatchAsJsonAsync($"{BaseUrl}/{created.Id}", new { code = "LOCK-3" });
        await AssertProblemAsync(changeCodeDisabled, HttpStatusCode.Conflict, CatalogErrorCode.EquipmentType.CodeLocked);
    }

    [Fact]
    public async Task PublicList_FiltersByCategory_AndHidesTypesOfInactiveCategories()
    {
        using EquipmentTypeApiFactory factory = new();
        Category forklifts = await factory.SeedCategoryAsync();
        Category excavators = await factory.SeedCategoryAsync(name: "Máy xúc");
        using HttpClient admin = factory.CreateAdminClient();
        using HttpClient anonymous = factory.CreateClient();
        EquipmentTypeManagementResponse forklift = await CreateAsync(admin, forklifts.Id, code: "F-1", name: "Forklift");
        EquipmentTypeManagementResponse excavator = await CreateAsync(admin, excavators.Id, code: "E-1", name: "Excavator");
        await CreateAsync(admin, excavators.Id, code: "E-2", name: "Draft excavator");
        (await admin.PostAsync($"{BaseUrl}/{forklift.Id}/publish", null)).Dispose();
        (await admin.PostAsync($"{BaseUrl}/{excavator.Id}/publish", null)).Dispose();

        Assert.Equal(["Excavator", "Forklift"], (await ListPublicAsync(anonymous)).Select(type => type.Name));
        Assert.Equal([excavator.Id], (await ListPublicAsync(anonymous, excavators.Id)).Select(type => type.Id));

        await factory.WithDbContextAsync(async db =>
        {
            Category stored = await db.Categories.SingleAsync(c => c.Id == excavators.Id);
            stored.IsActive = false;
            await db.SaveChangesAsync();
        });

        Assert.Equal([forklift.Id], (await ListPublicAsync(anonymous)).Select(type => type.Id));
        using HttpResponseMessage hiddenDetail = await anonymous.GetAsync($"{BaseUrl}/{excavator.Id}");
        Assert.Equal(HttpStatusCode.NotFound, hiddenDetail.StatusCode);
    }

    [Fact]
    public async Task EquipmentType_IsNeverDeleted_NoDeleteEndpointExists()
    {
        using EquipmentTypeApiFactory factory = new();
        Category category = await factory.SeedCategoryAsync();
        using HttpClient admin = factory.CreateAdminClient();
        EquipmentTypeManagementResponse created = await CreateAsync(admin, category.Id);

        using HttpResponseMessage delete = await admin.DeleteAsync($"{BaseUrl}/{created.Id}");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        Assert.Equal(created.Id, (await GetAsync(admin, created.Id)).Id);
    }

    internal static object ValidRequest(Guid categoryId, string code = "FORKLIFT-1.5-3.5", string name = "Forklift 1.5–3.5T") => new
    {
        categoryId,
        code,
        name,
        description = "Xe nâng tải trọng 1.5 đến 3.5 tấn"
    };

    internal static async Task<EquipmentTypeManagementResponse> CreateAsync(
        HttpClient admin,
        Guid categoryId,
        string code = "FORKLIFT-1.5-3.5",
        string name = "Forklift 1.5–3.5T")
    {
        using HttpResponseMessage response = await admin.PostAsJsonAsync(BaseUrl, ValidRequest(categoryId, code, name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    internal static async Task<EquipmentTypeManagementResponse> GetAsync(HttpClient admin, Guid id)
    {
        using HttpResponseMessage response = await admin.GetAsync($"{BaseUrl}/{id}/management");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    private static async Task<IReadOnlyList<EquipmentTypeResponse>> ListPublicAsync(HttpClient client, Guid? categoryId = null)
    {
        string url = categoryId is null ? BaseUrl : $"{BaseUrl}?categoryId={categoryId}";
        using HttpResponseMessage response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<EquipmentTypeResponse>>(JsonSerializerOptions.Web))!;
    }

    internal static async Task<EquipmentTypeManagementResponse> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<EquipmentTypeManagementResponse>(JsonSerializerOptions.Web))!;

    internal static async Task<ProblemDetails> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        ProblemDetails problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>(JsonSerializerOptions.Web))!;
        Assert.Equal(code, problem.Title);
        Assert.Equal((int)status, problem.Status);
        return problem;
    }

    private sealed class FakeSpecSchemaCatalog : ISpecSchemaCatalog
    {
        public HashSet<Guid> PublishedFor { get; } = [];

        public Task<bool> HasPublishedSchemaAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(PublishedFor.Contains(equipmentTypeId));
    }
}
