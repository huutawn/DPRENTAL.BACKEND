using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.CatalogService.Api.Security;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Common.Security;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class EquipmentTypeSpecSchemaEndpointTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;
    private readonly CatalogServiceDbContext dbContext;

    private static readonly string AdminToken = TestJwtHelper.GenerateToken(
        sub: Guid.NewGuid().ToString(),
        sid: Guid.NewGuid().ToString(),
        role: CatalogClaims.Roles.Admin);

    private static readonly string OwnerToken = TestJwtHelper.GenerateToken(
        sub: Guid.NewGuid().ToString(),
        sid: Guid.NewGuid().ToString(),
        role: CatalogClaims.Roles.Owner);

    private static readonly string CustomerToken = TestJwtHelper.GenerateToken(
        sub: Guid.NewGuid().ToString(),
        sid: Guid.NewGuid().ToString(),
        role: CatalogClaims.Roles.Customer);

    public EquipmentTypeSpecSchemaEndpointTests()
    {
        factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", PostgreSqlFixture.ConnectionString);
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseTestJwks();
            });

        client = factory.CreateClient();
        dbContext = factory.Services.CreateScope().ServiceProvider.GetRequiredService<CatalogServiceDbContext>();
    }

    public async Task InitializeAsync()
    {
        await dbContext.Database.MigrateAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE \"catalog\".\"EquipmentTypeSpecSchemas\", \"catalog\".\"EquipmentTypeCategories\", \"catalog\".\"EquipmentTypeMedia\", \"catalog\".\"EquipmentTypes\", \"catalog\".\"Categories\" CASCADE;");
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
    }

    private void Authenticate(string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private void ClearAuthentication() =>
        client.DefaultRequestHeaders.Authorization = null;

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

    private async Task<EquipmentType> CreateEquipmentTypeAsync(string code, bool withPublishedSchema = false)
    {
        var category = await CreateCategoryAsync($"Cat {code}");
        var eq = new EquipmentType
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = $"Name {code}",
            CreatedAt = DateTime.UtcNow,
            Categories = [category]
        };
        dbContext.EquipmentTypes.Add(eq);
        await dbContext.SaveChangesAsync();

        if (withPublishedSchema)
        {
            var schema = EquipmentTypeSpecSchema.CreatePublished(
                Guid.NewGuid(),
                eq.Id,
                1,
                new EquipmentTypeSpecDefinition
                {
                    Fields = [new EquipmentTypeSpecField { Key = "defaultField", Label = "Mặc định", DataType = "text" }]
                },
                eq.CreatedAt,
                eq.CreatedAt);
            dbContext.EquipmentTypeSpecSchemas.Add(schema);
            eq.ApplyPublishedSchema(schema, eq.CreatedAt);
            await dbContext.SaveChangesAsync();
        }

        return eq;
    }

    // ==========================================
    // 1. VALIDATION TESTS
    // ==========================================

    [Fact]
    public async Task SpecSchema_AllowsEmptyFieldsArray()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("VALID-EMPTY");

        string body = """{ "fields": [] }""";
        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = await res.Content.ReadFromJsonAsync<EquipmentTypeSpecSchemaDto>();
        Assert.NotNull(created);
        Assert.Equal(1, created.Version);
        Assert.Empty(created.Definition.Fields);
    }

    [Fact]
    public async Task SpecSchema_MalformedOrNonObject_Returns400()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("VALID-NONOBJ");

        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent("\"not-an-object\"", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SpecSchema_UnknownRootProperties_Returns400()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("VALID-ROOTPROP");

        string body = """{ "fields": [], "unknownProp": 123 }""";
        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    public async Task SpecSchema_InvalidKey_Returns400(string invalidKey)
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync($"VALID-KEY-{Guid.NewGuid():N}");

        string body = $$"""
            {
                "fields": [
                    { "key": "{{invalidKey}}", "label": "Nhãn", "dataType": "text" }
                ]
            }
            """;
        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SpecSchema_DuplicateKey_OrdinalComparison_Returns400()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("VALID-DUPKEY");

        string body = """
            {
                "fields": [
                    { "key": "resolution", "label": "Độ phân giải", "dataType": "text" },
                    { "key": "resolution", "label": "Độ phân giải 2", "dataType": "text" }
                ]
            }
            """;
        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData("invalidType")]
    [InlineData("NUMBER")]
    [InlineData("object")]
    [InlineData("array")]
    public async Task SpecSchema_UnsupportedDataType_Returns400(string badType)
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync($"VALID-TYPE-{Guid.NewGuid():N}");

        string body = $$"""
            {
                "fields": [
                    { "key": "f1", "label": "Nhãn", "dataType": "{{badType}}" }
                ]
            }
            """;
        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SpecSchema_NumberBounds_MinGreaterThanMax_Returns400()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("VALID-BOUNDS");

        string body = """
            {
                "fields": [
                    { "key": "weight", "label": "Khối lượng", "dataType": "number", "min": 10.5, "max": 5.2 }
                ]
            }
            """;
        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SpecSchema_NonNumber_WithBounds_Returns400()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("VALID-BOUNDS-NONNUM");

        string body = """
            {
                "fields": [
                    { "key": "color", "label": "Màu", "dataType": "text", "min": 1 }
                ]
            }
            """;
        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SpecSchema_Enum_WithoutOptions_OrEmptyOptions_Returns400()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("VALID-ENUM-EMPTY");

        string body1 = """
            {
                "fields": [
                    { "key": "lensMount", "label": "Ngàm", "dataType": "enum" }
                ]
            }
            """;
        var res1 = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body1, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res1.StatusCode);

        string body2 = """
            {
                "fields": [
                    { "key": "lensMount", "label": "Ngàm", "dataType": "enum", "options": [] }
                ]
            }
            """;
        var res2 = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body2, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res2.StatusCode);
    }

    [Fact]
    public async Task SpecSchema_Enum_DuplicateOptionValues_Returns400()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("VALID-ENUM-DUP");

        string body = """
            {
                "fields": [
                    {
                        "key": "lensMount",
                        "label": "Ngàm",
                        "dataType": "enum",
                        "options": [
                            { "value": "e_mount", "label": "Sony E" },
                            { "value": "e_mount", "label": "Sony E-mount" }
                        ]
                    }
                ]
            }
            """;
        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SpecSchema_NonEnum_WithOptions_Returns400()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("VALID-NONENUM-OPT");

        string body = """
            {
                "fields": [
                    {
                        "key": "sensor",
                        "label": "Cảm biến",
                        "dataType": "text",
                        "options": [
                            { "value": "fullframe", "label": "Full Frame" }
                        ]
                    }
                ]
            }
            """;
        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SpecSchema_NegativeDisplayOrder_Returns400()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("VALID-DISPORDER");

        string body = """
            {
                "fields": [
                    { "key": "f1", "label": "F1", "dataType": "text", "displayOrder": -1 }
                ]
            }
            """;
        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SpecSchema_ValidCompleteDefinition_PreservesOrderAndDecimals()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("VALID-COMPLETE");

        string body = """
            {
                "fields": [
                    { "key": "f1", "label": "First", "dataType": "text", "displayOrder": 2 },
                    { "key": "weight", "label": "Weight", "dataType": "number", "min": 0.25, "max": 99.99, "unit": "kg", "displayOrder": 0 },
                    { "key": "mount", "label": "Mount", "dataType": "enum", "options": [{ "value": "e", "label": "E" }, { "value": "rf", "label": "RF" }] },
                    { "key": "active", "label": "Active", "dataType": "boolean", "required": true }
                ]
            }
            """;
        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var schema = await res.Content.ReadFromJsonAsync<EquipmentTypeSpecSchemaDto>();
        Assert.NotNull(schema);
        Assert.Equal(4, schema.Definition.Fields.Count);

        // Thứ tự fields được bảo toàn nguyên bản
        Assert.Equal("f1", schema.Definition.Fields[0].Key);
        Assert.Equal("weight", schema.Definition.Fields[1].Key);
        Assert.Equal(0.25m, schema.Definition.Fields[1].Min);
        Assert.Equal(99.99m, schema.Definition.Fields[1].Max);
        Assert.Equal("mount", schema.Definition.Fields[2].Key);
        Assert.Equal("active", schema.Definition.Fields[3].Key);
        Assert.True(schema.Definition.Fields[3].Required);
    }

    // ==========================================
    // 2. LIFECYCLE TESTS
    // ==========================================

    [Fact]
    public async Task Lifecycle_DraftExists_CreateNewDraft_Returns409Conflict()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("LIFE-DRAFT-CONFLICT");

        // Tạo draft v1
        string body = """{ "fields": [{ "key": "v1", "label": "V1", "dataType": "text" }] }""";
        var res1 = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);

        // Tạo tiếp draft khi v1 chưa publish -> 409 Conflict
        var res2 = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);
        var problem = await res2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(CatalogErrorCode.EquipmentType.DraftAlreadyExists, problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Lifecycle_EditDraft_ReplacesEntireDefinition()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("LIFE-EDIT-DRAFT");

        // Tạo draft v1
        string v1 = """{ "fields": [{ "key": "initial", "label": "Initial", "dataType": "text" }] }""";
        await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(v1, Encoding.UTF8, "application/json"));

        // PUT thay thế toàn bộ definition
        string v1Updated = """{ "fields": [{ "key": "replaced", "label": "Replaced", "dataType": "number" }] }""";
        var putRes = await client.PutAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1",
            new StringContent(v1Updated, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);
        var updated = await putRes.Content.ReadFromJsonAsync<EquipmentTypeSpecSchemaDto>();
        Assert.NotNull(updated);
        Assert.Single(updated.Definition.Fields);
        Assert.Equal("replaced", updated.Definition.Fields[0].Key);
    }

    [Fact]
    public async Task Lifecycle_PublishDraft_UpdatesParentCurrentVersion_DoesNotAutoPublishEquipmentType()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("LIFE-PUB-SCHEMA");

        // Tạo draft v1
        string v1 = """{ "fields": [{ "key": "iso", "label": "ISO", "dataType": "number" }] }""";
        await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(v1, Encoding.UTF8, "application/json"));

        // Publish schema v1
        var pubRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1/publish", null);
        Assert.Equal(HttpStatusCode.OK, pubRes.StatusCode);
        var pubSchema = await pubRes.Content.ReadFromJsonAsync<EquipmentTypeSpecSchemaDto>();
        Assert.NotNull(pubSchema);
        Assert.Equal("Published", pubSchema.Status);
        Assert.NotNull(pubSchema.PublishedAt);

        // Kiểm tra parent: CurrentSchemaVersion = 1 nhưng status vẫn là Draft!
        var parentInDb = await dbContext.EquipmentTypes.AsNoTracking().SingleAsync(e => e.Id == eq.Id);
        Assert.Equal(1, parentInDb.CurrentSchemaVersion);
        Assert.Equal(EquipmentTypeStatus.Draft, parentInDb.Status);

        // Bây giờ mới có thể publish EquipmentType
        var pubEqRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, pubEqRes.StatusCode);
        var pubEqDetail = await pubEqRes.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(pubEqDetail);
        Assert.Equal(EquipmentTypeStatus.Published, pubEqDetail.Status);
        Assert.Equal(1, pubEqDetail.CurrentSchemaVersion);
        Assert.NotNull(pubEqDetail.SpecSchema);
        Assert.Equal(1, pubEqDetail.SpecSchema.Version);
    }

    [Fact]
    public async Task Lifecycle_ModifyOrRepublish_PublishedSchema_Returns409Conflict()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("LIFE-PUB-IMMUTABLE");

        // Tạo và publish v1
        string v1 = """{ "fields": [{ "key": "f1", "label": "F1", "dataType": "text" }] }""";
        await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas", new StringContent(v1, Encoding.UTF8, "application/json"));
        await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1/publish", null);

        // Cố gắng sửa v1 đã publish -> 409 Conflict
        var editRes = await client.PutAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1", new StringContent(v1, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, editRes.StatusCode);
        var problemEdit = await editRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(CatalogErrorCode.EquipmentType.SchemaAlreadyPublished, problemEdit.GetProperty("title").GetString());

        // Cố gắng publish lại v1 đã publish -> 409 Conflict
        var pubAgainRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1/publish", null);
        Assert.Equal(HttpStatusCode.Conflict, pubAgainRes.StatusCode);
        var problemPub = await pubAgainRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(CatalogErrorCode.EquipmentType.SchemaAlreadyPublished, problemPub.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Lifecycle_PublishV2_KeepsV1Intact_UpdatesCurrentVersion()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("LIFE-V1-TO-V2");

        // 1. Tạo v1 và publish
        string v1Def = """{ "fields": [{ "key": "v1Field", "label": "V1", "dataType": "text" }] }""";
        await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas", new StringContent(v1Def, Encoding.UTF8, "application/json"));
        await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1/publish", null);

        // 2. Tạo v2 draft
        string v2Def = """{ "fields": [{ "key": "v2Field", "label": "V2", "dataType": "number" }] }""";
        var createV2Res = await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas", new StringContent(v2Def, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, createV2Res.StatusCode);
        var draftV2 = await createV2Res.Content.ReadFromJsonAsync<EquipmentTypeSpecSchemaDto>();
        Assert.Equal(2, draftV2!.Version);
        Assert.Equal("Draft", draftV2.Status);

        // 3. Publish v2
        var pubV2Res = await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/2/publish", null);
        Assert.Equal(HttpStatusCode.OK, pubV2Res.StatusCode);

        // 4. Kiểm tra current version là 2
        var parentInDb = await dbContext.EquipmentTypes.AsNoTracking().SingleAsync(e => e.Id == eq.Id);
        Assert.Equal(2, parentInDb.CurrentSchemaVersion);

        // 5. Kiểm tra v1 vẫn nguyên vẹn
        var getV1Res = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1");
        Assert.Equal(HttpStatusCode.OK, getV1Res.StatusCode);
        var v1Loaded = await getV1Res.Content.ReadFromJsonAsync<EquipmentTypeSpecSchemaDto>();
        Assert.NotNull(v1Loaded);
        Assert.Equal(1, v1Loaded.Version);
        Assert.Equal("v1Field", v1Loaded.Definition.Fields[0].Key);
    }

    [Fact]
    public async Task Lifecycle_DisableAndRepublishType_DoesNotChangeDefinitionOrCurrentVersion()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("LIFE-DIS-REPUB", withPublishedSchema: true);

        // Publish EquipmentType
        await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/publish", null);

        // Disable EquipmentType
        var disRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disRes.StatusCode);
        var disDetail = await disRes.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.Equal(1, disDetail!.CurrentSchemaVersion);

        // Re-publish EquipmentType
        var repubRes = await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, repubRes.StatusCode);
        var repubDetail = await repubRes.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.Equal(1, repubDetail!.CurrentSchemaVersion);
    }

    // ==========================================
    // 3. AUTHORIZATION & PUBLIC ACCESS TESTS
    // ==========================================

    [Fact]
    public async Task SpecSchema_WriteEndpoints_RejectNonAdminRoles()
    {
        var eq = await CreateEquipmentTypeAsync("AUTH-WRITE");
        string body = """{ "fields": [] }""";

        // Owner -> 403
        Authenticate(OwnerToken);
        var resPost = await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, resPost.StatusCode);

        var resPut = await client.PutAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, resPut.StatusCode);

        var resPub = await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1/publish", null);
        Assert.Equal(HttpStatusCode.Forbidden, resPub.StatusCode);

        // Customer -> 403
        Authenticate(CustomerToken);
        var resCus = await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, resCus.StatusCode);

        // Anonymous -> 401
        ClearAuthentication();
        var resAnon = await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, resAnon.StatusCode);
    }

    [Fact]
    public async Task SpecSchema_GetAllVersions_AdminOnly()
    {
        var eq = await CreateEquipmentTypeAsync("AUTH-GETALL", withPublishedSchema: true);

        // Admin -> 200
        Authenticate(AdminToken);
        var adminRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas");
        Assert.Equal(HttpStatusCode.OK, adminRes.StatusCode);
        var list = await adminRes.Content.ReadFromJsonAsync<IReadOnlyList<EquipmentTypeSpecSchemaDto>>();
        Assert.NotEmpty(list!);

        // Non-admin -> 403
        Authenticate(OwnerToken);
        var ownerRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas");
        Assert.Equal(HttpStatusCode.Forbidden, ownerRes.StatusCode);

        // Anonymous -> 401
        ClearAuthentication();
        var anonRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas");
        Assert.Equal(HttpStatusCode.Unauthorized, anonRes.StatusCode);
    }

    [Fact]
    public async Task SpecSchema_PublicReadByVersion_NeverExposesDraftSchema()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("PUB-READ-DRAFT");

        // Tạo draft v1
        string v1 = """{ "fields": [{ "key": "secret", "label": "Draft Field", "dataType": "text" }] }""";
        await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas", new StringContent(v1, Encoding.UTF8, "application/json"));

        // Caller Public (Anonymous) đọc draft v1 -> 404 Not Found (không lộ Draft)
        ClearAuthentication();
        var anonRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1");
        Assert.Equal(HttpStatusCode.NotFound, anonRes.StatusCode);

        // Admin đọc draft v1 -> 200 OK
        Authenticate(AdminToken);
        var adminRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1");
        Assert.Equal(HttpStatusCode.OK, adminRes.StatusCode);
    }

    [Fact]
    public async Task SpecSchema_PublicReadByVersion_ReadsPublishedSchema_WhenPublishedOrDisabled()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("PUB-READ-HIST", withPublishedSchema: true);

        // 1. Khi EquipmentType đang Draft -> Public không đọc được (404)
        ClearAuthentication();
        var draftRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1");
        Assert.Equal(HttpStatusCode.NotFound, draftRes.StatusCode);

        // 2. Publish EquipmentType -> Public đọc được (200)
        Authenticate(AdminToken);
        await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/publish", null);

        ClearAuthentication();
        var pubRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1");
        Assert.Equal(HttpStatusCode.OK, pubRes.StatusCode);

        // 3. Disable EquipmentType -> Public vẫn đọc được theo version (200)
        Authenticate(AdminToken);
        await client.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/disable", null);

        ClearAuthentication();
        var disRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1");
        Assert.Equal(HttpStatusCode.OK, disRes.StatusCode);
        var schema = await disRes.Content.ReadFromJsonAsync<EquipmentTypeSpecSchemaDto>();
        Assert.NotNull(schema);
        Assert.Equal(1, schema.Version);
        Assert.Equal("Published", schema.Status);
    }

    // ==========================================
    // 4. POST-REVIEW TESTS: FLAGS, CASING, IMMUTABILITY & CONCURRENCY
    // ==========================================

    [Fact]
    public async Task Create_WithOriginalSamplePayload_SavesDraftV1WithFlags()
    {
        Authenticate(AdminToken);
        var category = await CreateCategoryAsync("Camera Samples", isActive: true);

        string originalSamplePayload = $$"""
            {
                "code": "CAMERA-ORIGINAL-SAMPLE",
                "name": "Sony Alpha A7 IV",
                "description": "Full-frame hybrid camera",
                "categoryIds": ["{{category.Id}}"],
                "specSchema": {
                    "fields": [
                        {
                            "key": "sensor_type",
                            "label": "Sensor Type",
                            "dataType": "enum",
                            "required": true,
                            "filterable": true,
                            "primary": true,
                            "displayOrder": 1,
                            "options": [
                                { "value": "full_frame", "label": "Full Frame" },
                                { "value": "aps_c", "label": "APS-C" }
                            ]
                        },
                        {
                            "key": "megapixels",
                            "label": "Resolution (MP)",
                            "dataType": "number",
                            "required": false,
                            "filterable": true,
                            "primary": false,
                            "displayOrder": 2,
                            "min": 1.0,
                            "max": 100.0,
                            "unit": "MP"
                        }
                    ]
                }
            }
            """;

        var postRes = await client.PostAsync(
            "/api/v1/catalog/equipment-types",
            new StringContent(originalSamplePayload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, postRes.StatusCode);
        var createdType = await postRes.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(createdType);

        var schemaRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{createdType.Id}/spec-schemas/1");
        Assert.Equal(HttpStatusCode.OK, schemaRes.StatusCode);
        var schema = await schemaRes.Content.ReadFromJsonAsync<EquipmentTypeSpecSchemaDto>();
        Assert.NotNull(schema);
        Assert.Equal(2, schema.Definition.Fields.Count);

        var f1 = schema.Definition.Fields[0];
        Assert.Equal("sensor_type", f1.Key);
        Assert.True(f1.Required);
        Assert.True(f1.Filterable);
        Assert.True(f1.Primary);
        Assert.Equal(2, f1.Options?.Count);

        var f2 = schema.Definition.Fields[1];
        Assert.Equal("megapixels", f2.Key);
        Assert.False(f2.Required);
        Assert.True(f2.Filterable);
        Assert.False(f2.Primary);
        Assert.Equal(1.0m, f2.Min);
        Assert.Equal(100.0m, f2.Max);
    }

    [Fact]
    public async Task SpecSchema_PropertyCasing_ParsesCorrectly_WithoutDataLoss()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("CASING-TEST");

        string body = """
            {
                "FIELDS": [
                    {
                        "KEY": "sensor",
                        "LABEL": "Sensor Format",
                        "DATATYPE": "text",
                        "REQUIRED": true,
                        "Filterable": true,
                        "PRIMARY": true,
                        "DisplayOrder": 3
                    }
                ]
            }
            """;

        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = await res.Content.ReadFromJsonAsync<EquipmentTypeSpecSchemaDto>();
        Assert.NotNull(created);
        Assert.Single(created.Definition.Fields);

        var field = created.Definition.Fields[0];
        Assert.Equal("sensor", field.Key);
        Assert.Equal("Sensor Format", field.Label);
        Assert.Equal("text", field.DataType);
        Assert.True(field.Required);
        Assert.True(field.Filterable);
        Assert.True(field.Primary);
        Assert.Equal(3, field.DisplayOrder);
    }

    [Fact]
    public async Task SpecSchema_DuplicateProperty_DifferentCasing_Returns400()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("DUP-PROP-TEST");

        string body = """
            {
                "fields": [
                    {
                        "key": "f1",
                        "label": "L1",
                        "dataType": "text",
                        "required": true,
                        "Required": false
                    }
                ]
            }
            """;

        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SpecSchema_WithPlaceholder_Returns400()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("PLACEHOLDER-TEST");

        string body = """
            {
                "fields": [
                    {
                        "key": "f1",
                        "label": "L1",
                        "dataType": "text",
                        "placeholder": "Enter text..."
                    }
                ]
            }
            """;

        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Validation_NonEnumWithOptionsEmpty_Returns400()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("NONENUM-EMPTYOPT");

        string jsonBody = """
            {
                "fields": [
                    { "key": "f1", "label": "L1", "dataType": "text", "options": [] }
                ]
            }
            """;

        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(jsonBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public void Validation_TypedNonEnumWithOptionsEmpty_ReturnsFailure()
    {
        var typedDef = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField
                {
                    Key = "text_field",
                    Label = "Text Field",
                    DataType = "text",
                    Options = []
                }
            ]
        };

        var result = Dprental.CatalogService.Domain.Validation.SpecSchemaDefinitionValidator.Validate(typedDef);
        Assert.True(result.IsFailure);
        Assert.Contains("options", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validation_TypedNullFieldOrNullOption_ReturnsFailure()
    {
        var defWithNullField = new EquipmentTypeSpecDefinition
        {
            Fields = [null!]
        };
        var res1 = Dprental.CatalogService.Domain.Validation.SpecSchemaDefinitionValidator.Validate(defWithNullField);
        Assert.True(res1.IsFailure);

        var defWithNullOption = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField
                {
                    Key = "enum_field",
                    Label = "Enum Field",
                    DataType = "enum",
                    Options = [null!]
                }
            ]
        };
        var res2 = Dprental.CatalogService.Domain.Validation.SpecSchemaDefinitionValidator.Validate(defWithNullOption);
        Assert.True(res2.IsFailure);
    }

    [Fact]
    public void Domain_Definition_DefensiveCopy_PreventsExternalMutation()
    {
        var mutableOptions = new List<EquipmentTypeSpecOption>
        {
            new() { Value = "opt1", Label = "Option 1" }
        };

        var mutableFields = new List<EquipmentTypeSpecField>
        {
            new()
            {
                Key = "f1",
                Label = "L1",
                DataType = "enum",
                Options = mutableOptions
            }
        };

        var definition = new EquipmentTypeSpecDefinition { Fields = mutableFields };

        // Thay đổi list nguồn ngoài
        mutableFields.Clear();
        mutableOptions.Clear();

        // Definition bên trong không bị ảnh hưởng
        Assert.Single(definition.Fields);
        Assert.Equal("f1", definition.Fields[0].Key);
        Assert.Single(definition.Fields[0].Options!);
        Assert.Equal("opt1", definition.Fields[0].Options![0].Value);
    }

    [Fact]
    public void Domain_PublishedSchema_CannotBeModifiedOrRepublished()
    {
        DateTime now = DateTime.UtcNow;
        var schema = EquipmentTypeSpecSchema.CreateDraft(
            Guid.NewGuid(), 1, new EquipmentTypeSpecDefinition(), now);

        schema.Publish(now);

        // Thử sửa definition -> ném InvalidOperationException
        Assert.Throws<InvalidOperationException>(() =>
            schema.UpdateDefinition(new EquipmentTypeSpecDefinition(), now));

        // Thử publish lại -> ném InvalidOperationException
        Assert.Throws<InvalidOperationException>(() =>
            schema.Publish(now));
    }

    // ==========================================
    // 5. POSTGRESQL CONCURRENCY & RACE TESTS
    // ==========================================

    [Fact]
    public async Task Race_CreateDraft_ConcurrentCalls_OneSucceeds_OneReturnsConflict()
    {
        var eq = await CreateEquipmentTypeAsync("RACE-CREATE");
        string body = """{ "fields": [{ "key": "f1", "label": "L1", "dataType": "text" }] }""";

        using var client1 = factory.CreateClient();
        using var client2 = factory.CreateClient();
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);

        using var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await client1.PostAsync(
                $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
                new StringContent(body, Encoding.UTF8, "application/json"));
        });

        var task2 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await client2.PostAsync(
                $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
                new StringContent(body, Encoding.UTF8, "application/json"));
        });

        var results = await Task.WhenAll(task1, task2);

        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Race_EditDraft_ConcurrentCalls_OneSucceeds_OneReturnsConcurrencyConflict()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("RACE-EDIT");

        // Tạo draft v1
        string initialDef = """{ "fields": [{ "key": "v1", "label": "V1", "dataType": "text" }] }""";
        await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(initialDef, Encoding.UTF8, "application/json"));

        string edit1 = """{ "fields": [{ "key": "v1", "label": "Edit 1", "dataType": "text" }] }""";
        string edit2 = """{ "fields": [{ "key": "v1", "label": "Edit 2", "dataType": "text" }] }""";

        using var client1 = factory.CreateClient();
        using var client2 = factory.CreateClient();
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);

        using var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await client1.PutAsync(
                $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1",
                new StringContent(edit1, Encoding.UTF8, "application/json"));
        });

        var task2 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await client2.PutAsync(
                $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1",
                new StringContent(edit2, Encoding.UTF8, "application/json"));
        });

        var results = await Task.WhenAll(task1, task2);

        // 1 cuộc gọi thành công 200, cuộc gọi kia dính optimistic concurrency conflict 409
        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Race_PublishDraft_ConcurrentCalls_OneSucceeds_OneReturnsConflict()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeAsync("RACE-PUB");

        // Tạo draft v1
        string v1 = """{ "fields": [{ "key": "f1", "label": "L1", "dataType": "text" }] }""";
        await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(v1, Encoding.UTF8, "application/json"));

        using var client1 = factory.CreateClient();
        using var client2 = factory.CreateClient();
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);

        using var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await client1.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1/publish", null);
        });

        var task2 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await client2.PostAsync($"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1/publish", null);
        });

        var results = await Task.WhenAll(task1, task2);

        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("POST", "spec-schemas")]
    [InlineData("PUT", "spec-schemas/1")]
    [InlineData("POST", "spec-schemas/1/publish")]
    [InlineData("GET", "spec-schemas")]
    public async Task SpecSchema_AuthorizationMatrix_EnforcesAccessPolicies(string method, string subPath)
    {
        var eq = await CreateEquipmentTypeAsync($"AUTH-MAT-{Guid.NewGuid():N}");
        string url = $"/api/v1/catalog/equipment-types/{eq.Id}/{subPath}";
        string body = """{ "fields": [] }""";

        // 1. Anonymous -> 401
        ClearAuthentication();
        using var anonReq = new HttpRequestMessage(new HttpMethod(method), url);
        if (method != "GET")
        {
            anonReq.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        var anonRes = await client.SendAsync(anonReq);
        Assert.Equal(HttpStatusCode.Unauthorized, anonRes.StatusCode);

        // 2. Roles: own, adp, mem, cus, svc -> 403
        string[] forbiddenRoles = ["own", "adp", "mem", "cus"];
        foreach (var role in forbiddenRoles)
        {
            Authenticate(TestJwtHelper.GenerateToken(
                sub: Guid.NewGuid().ToString(),
                sid: Guid.NewGuid().ToString(),
                role: role));

            using var forbiddenReq = new HttpRequestMessage(new HttpMethod(method), url);
            if (method != "GET")
            {
                forbiddenReq.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            var forbiddenRes = await client.SendAsync(forbiddenReq);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenRes.StatusCode);
        }

        // 3. Service token -> 403
        Authenticate(TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: Guid.NewGuid().ToString(),
            role: "adm",
            tokenUse: "svc"));

        using var svcReq = new HttpRequestMessage(new HttpMethod(method), url);
        if (method != "GET")
        {
            svcReq.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        var svcRes = await client.SendAsync(svcReq);
        Assert.Equal(HttpStatusCode.Forbidden, svcRes.StatusCode);
    }
}
