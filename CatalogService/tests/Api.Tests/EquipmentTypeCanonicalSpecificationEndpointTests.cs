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
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.AspNetCore.TestHost;
using Dprental.CatalogService.Domain.Validation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class EquipmentTypeCanonicalSpecificationEndpointTests : IAsyncLifetime
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

    private static readonly string ServiceToken = TestJwtHelper.GenerateToken(
        sub: Guid.NewGuid().ToString(),
        tokenUse: CatalogClaims.TokenUses.Service,
        role: null);

    public EquipmentTypeCanonicalSpecificationEndpointTests()
    {
        factory = new CatalogWebApplicationFactory()
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
            "TRUNCATE TABLE \"catalog\".\"EquipmentTypeSpecSchemas\", \"catalog\".\"EquipmentTypeCategories\", \"catalog\".\"EquipmentTypeMedia\", \"catalog\".\"EquipmentTypes\", \"catalog\".\"Categories\", \"catalog\".\"Brands\" CASCADE;");
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

    private async Task<Category> CreateCategoryAsync(string name)
    {
        Category cat = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        dbContext.Categories.Add(cat);
        await dbContext.SaveChangesAsync();
        return cat;
    }

    private async Task<EquipmentType> CreateEquipmentTypeWithSchemaAsync(
        string code,
        EquipmentTypeStatus status = EquipmentTypeStatus.Published)
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

        var schemaDefinition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField
                {
                    Key = "resolution",
                    Label = "Độ phân giải",
                    DataType = "text",
                    Required = true,
                    Primary = true,
                    DisplayOrder = 2
                },
                new EquipmentTypeSpecField
                {
                    Key = "weight",
                    Label = "Khối lượng",
                    DataType = "number",
                    Required = true,
                    Primary = true,
                    DisplayOrder = 1,
                    Min = 100,
                    Max = 5000,
                    Unit = "g"
                },
                new EquipmentTypeSpecField
                {
                    Key = "mount",
                    Label = "Ngàm",
                    DataType = "enum",
                    Required = false,
                    Primary = false,
                    DisplayOrder = 3,
                    Options =
                    [
                        new EquipmentTypeSpecOption { Value = "e_mount", Label = "E-Mount" },
                        new EquipmentTypeSpecOption { Value = "rf_mount", Label = "RF-Mount" }
                    ]
                }
            ]
        };

        var schema = EquipmentTypeSpecSchema.CreatePublished(
            Guid.NewGuid(),
            eq.Id,
            1,
            schemaDefinition,
            eq.CreatedAt,
            eq.CreatedAt);

        dbContext.EquipmentTypeSpecSchemas.Add(schema);
        eq.ApplyPublishedSchema(schema, eq.CreatedAt);

        if (status == EquipmentTypeStatus.Published)
        {
            eq.Publish(eq.CreatedAt);
        }
        else if (status == EquipmentTypeStatus.Disabled)
        {
            eq.Publish(eq.CreatedAt);
            eq.Disable(eq.CreatedAt);
        }

        await dbContext.SaveChangesAsync();
        return eq;
    }

    // ========================================================
    // 1. GET /schema tests
    // ========================================================

    [Fact]
    public async Task GetCurrentSchema_PublishedType_ReturnsCorrectShapeAndSortedFields()
    {
        ClearAuthentication();
        var eq = await CreateEquipmentTypeWithSchemaAsync("SCHEMA-PUB-01", EquipmentTypeStatus.Published);

        var res = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/schema");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var response = await res.Content.ReadFromJsonAsync<EquipmentTypePublishedSchemaResponse>();
        Assert.NotNull(response);
        Assert.Equal(eq.Id, response.EquipmentTypeId);
        Assert.Equal(1, response.Version);
        Assert.Equal(3, response.Fields.Count);

        // Fields must be ordered by DisplayOrder, then Key (Ordinal):
        // 1. weight (DisplayOrder: 1)
        // 2. resolution (DisplayOrder: 2)
        // 3. mount (DisplayOrder: 3)
        Assert.Equal("weight", response.Fields[0].Key);
        Assert.Equal("g", response.Fields[0].Unit);
        Assert.Equal(100m, response.Fields[0].Min);
        Assert.Equal(5000m, response.Fields[0].Max);

        Assert.Equal("resolution", response.Fields[1].Key);
        Assert.True(response.Fields[1].Primary);

        Assert.Equal("mount", response.Fields[2].Key);
        Assert.NotNull(response.Fields[2].Options);
        Assert.Equal(2, response.Fields[2].Options!.Count);
    }

    [Fact]
    public async Task GetCurrentSchema_DraftOrDisabledType_Returns404NotFound()
    {
        ClearAuthentication();
        var draftEq = await CreateEquipmentTypeWithSchemaAsync("SCHEMA-DRAFT-01", EquipmentTypeStatus.Draft);
        var disabledEq = await CreateEquipmentTypeWithSchemaAsync("SCHEMA-DIS-01", EquipmentTypeStatus.Disabled);

        var draftRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{draftEq.Id}/schema");
        Assert.Equal(HttpStatusCode.NotFound, draftRes.StatusCode);
        var draftProblem = await draftRes.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.NotFound, draftProblem!.Title);

        var disRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{disabledEq.Id}/schema");
        Assert.Equal(HttpStatusCode.NotFound, disRes.StatusCode);
        var disProblem = await disRes.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.NotFound, disProblem!.Title);
    }

    [Fact]
    public async Task GetCurrentSchema_NonExistentType_Returns404NotFound()
    {
        ClearAuthentication();
        var res = await client.GetAsync($"/api/v1/catalog/equipment-types/{Guid.NewGuid()}/schema");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.NotFound, problem!.Title);
    }

    // ========================================================
    // 2. GET /schemas/{version} tests
    // ========================================================

    [Fact]
    public async Task GetSchemaByVersion_PublishedAndDisabledTypes_Succeeds()
    {
        ClearAuthentication();
        var pubEq = await CreateEquipmentTypeWithSchemaAsync("SCH-VER-PUB", EquipmentTypeStatus.Published);
        var disEq = await CreateEquipmentTypeWithSchemaAsync("SCH-VER-DIS", EquipmentTypeStatus.Disabled);

        var pubRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{pubEq.Id}/schemas/1");
        Assert.Equal(HttpStatusCode.OK, pubRes.StatusCode);
        var pubSchema = await pubRes.Content.ReadFromJsonAsync<EquipmentTypePublishedSchemaResponse>();
        Assert.NotNull(pubSchema);
        Assert.Equal(1, pubSchema.Version);

        var disRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{disEq.Id}/schemas/1");
        Assert.Equal(HttpStatusCode.OK, disRes.StatusCode);
        var disSchema = await disRes.Content.ReadFromJsonAsync<EquipmentTypePublishedSchemaResponse>();
        Assert.NotNull(disSchema);
        Assert.Equal(1, disSchema.Version);
    }

    [Fact]
    public async Task GetSchemaByVersion_DraftType_Returns404NotFound()
    {
        ClearAuthentication();
        var draftEq = await CreateEquipmentTypeWithSchemaAsync("SCH-VER-DRAFT", EquipmentTypeStatus.Draft);

        var res = await client.GetAsync($"/api/v1/catalog/equipment-types/{draftEq.Id}/schemas/1");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.NotFound, problem!.Title);
    }

    [Fact]
    public async Task GetSchemaByVersion_NonExistentVersion_Returns404SchemaNotFound()
    {
        ClearAuthentication();
        var eq = await CreateEquipmentTypeWithSchemaAsync("SCH-VER-NONE", EquipmentTypeStatus.Published);

        var res = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/schemas/99");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.SchemaNotFound, problem!.Title);
    }

    [Fact]
    public async Task GetSchemaByVersion_ZeroOrNegativeVersion_Returns404SchemaNotFound()
    {
        ClearAuthentication();
        var eq = await CreateEquipmentTypeWithSchemaAsync("SCH-VER-ZERO", EquipmentTypeStatus.Published);

        var res0 = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/schemas/0");
        Assert.Equal(HttpStatusCode.NotFound, res0.StatusCode);
        var p0 = await res0.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.SchemaNotFound, p0!.Title);

        var resNeg = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/schemas/-1");
        Assert.Equal(HttpStatusCode.NotFound, resNeg.StatusCode);
        var pNeg = await resNeg.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.SchemaNotFound, pNeg!.Title);
    }

    // ========================================================
    // 3. POST /specifications/validate: Authorization Matrix
    // ========================================================

    [Fact]
    public async Task Validate_AnonymousOrInvalidJwt_Returns401()
    {
        var eq = await CreateEquipmentTypeWithSchemaAsync("AUTH-TEST-01", EquipmentTypeStatus.Published);

        string body = """{ "schemaVersion": 1, "specValues": { "resolution": "4K", "weight": 500 } }""";

        // 1. Anonymous
        ClearAuthentication();
        var anonRes = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonRes.StatusCode);

        // 2. Invalid JWT (random string)
        Authenticate("invalid-jwt-token");
        var invalidRes = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, invalidRes.StatusCode);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Owner")]
    [InlineData("Service")]
    [InlineData("Admin")]
    public async Task Validate_ValidUserOrServiceJwt_Allowed(string userType)
    {
        var eq = await CreateEquipmentTypeWithSchemaAsync($"AUTH-OK-{userType}", EquipmentTypeStatus.Published);

        string token = userType switch
        {
            "Customer" => CustomerToken,
            "Owner" => OwnerToken,
            "Service" => ServiceToken,
            _ => AdminToken
        };

        Authenticate(token);
        string body = """{ "schemaVersion": 1, "specValues": { "resolution": "4K", "weight": 500 } }""";

        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var valResult = await res.Content.ReadFromJsonAsync<SpecificationValidationResult>();
        Assert.NotNull(valResult);
        Assert.True(valResult.Valid);
    }

    [Fact]
    public async Task Validate_AdminCannotBypass_DraftOrDisabledType_Returns404()
    {
        Authenticate(AdminToken);

        var draftEq = await CreateEquipmentTypeWithSchemaAsync("BYPASS-DRAFT", EquipmentTypeStatus.Draft);
        var disabledEq = await CreateEquipmentTypeWithSchemaAsync("BYPASS-DIS", EquipmentTypeStatus.Disabled);

        string body = """{ "schemaVersion": 1, "specValues": { "resolution": "4K", "weight": 500 } }""";

        var resDraft = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{draftEq.Id}/specifications/validate",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, resDraft.StatusCode);
        var pDraft = await resDraft.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.NotFound, pDraft!.Title);

        var resDis = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{disabledEq.Id}/specifications/validate",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, resDis.StatusCode);
        var pDis = await resDis.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.NotFound, pDis!.Title);
    }

    [Fact]
    public async Task Validate_AdminCannotBypass_DraftSchema_Returns404SchemaNotFound()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeWithSchemaAsync("BYPASS-SCHEMA", EquipmentTypeStatus.Published);

        // Add a draft schema v2 (not published)
        var draftSchema = EquipmentTypeSpecSchema.CreateDraft(
            eq.Id,
            2,
            new EquipmentTypeSpecDefinition { Fields = [] },
            DateTime.UtcNow);
        dbContext.EquipmentTypeSpecSchemas.Add(draftSchema);
        await dbContext.SaveChangesAsync();

        string body = """{ "schemaVersion": 2, "specValues": {} }""";

        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.SchemaNotFound, problem!.Title);
    }

    [Fact]
    public async Task ManagementEndpoints_CustomerOrService_Returns403()
    {
        Authenticate(CustomerToken);
        var cat = await CreateCategoryAsync("CAT-MANAGE-TEST");

        // POST /equipment-types (Create)
        var createRes = await client.PostAsJsonAsync("/api/v1/catalog/equipment-types", new CreateEquipmentTypeRequest(
            "FAIL-CODE", "Fail Name", null, [cat.Id], null));
        Assert.Equal(HttpStatusCode.Forbidden, createRes.StatusCode);

        // Spec-schemas
        var schemaRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{Guid.NewGuid()}/spec-schemas");
        Assert.Equal(HttpStatusCode.Forbidden, schemaRes.StatusCode);
    }

    // ========================================================
    // 4. POST /specifications/validate: Values Validation Behavior
    // ========================================================

    [Fact]
    public async Task Validate_Success_Returns200WithEmptyErrors()
    {
        Authenticate(CustomerToken);
        var eq = await CreateEquipmentTypeWithSchemaAsync("VAL-SUCC-01", EquipmentTypeStatus.Published);

        string body = """
            {
                "schemaVersion": 1,
                "specValues": {
                    "resolution": "4K UHD",
                    "weight": 850,
                    "mount": "e_mount"
                }
            }
            """;

        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var result = await res.Content.ReadFromJsonAsync<SpecificationValidationResult>();
        Assert.NotNull(result);
        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
    }
    [Fact]
    public async Task Validate_Failure_Returns200WithDetailedErrors()
    {
        Authenticate(CustomerToken);
        var eq = await CreateEquipmentTypeWithSchemaAsync("VAL-FAIL-01", EquipmentTypeStatus.Published);

        // resolution is missing (REQUIRED)
        // weight is out of range (> 5000)
        // mount is invalid enum option
        // unknownProp is not in schema
        string body = """
            {
                "schemaVersion": 1,
                "specValues": {
                    "weight": 99999,
                    "mount": "Nikon-Z",
                    "extraField": 123
                }
            }
            """;

        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var result = await res.Content.ReadFromJsonAsync<SpecificationValidationResult>();
        Assert.NotNull(result);
        Assert.False(result.Valid);
        Assert.Equal(4, result.Errors.Count);

        // Schema order: resolution, weight, mount, then unknown: extraField
        Assert.Equal("resolution", result.Errors[0].Field);
        Assert.Equal(SpecificationValuesValidator.CodeRequired, result.Errors[0].Code);

        Assert.Equal("weight", result.Errors[1].Field);
        Assert.Equal(SpecificationValuesValidator.CodeOutOfRange, result.Errors[1].Code);

        Assert.Equal("mount", result.Errors[2].Field);
        Assert.Equal(SpecificationValuesValidator.CodeInvalidEnumOption, result.Errors[2].Code);

        Assert.Equal("extraField", result.Errors[3].Field);
        Assert.Equal(SpecificationValuesValidator.CodeUnknownField, result.Errors[3].Code);
    }

    // ========================================================
    // 5. POST /specifications/validate: Envelope & Duplicate Errors (400)
    // ========================================================

    [Theory]
    [InlineData("""{ "schemaVersion": 0, "specValues": {} }""")]
    [InlineData("""{ "schemaVersion": -1, "specValues": {} }""")]
    [InlineData("""{ "schemaVersion": "1", "specValues": {} }""")]
    [InlineData("""{ "schemaVersion": 1.5, "specValues": {} }""")]
    [InlineData("""{ "specValues": {} }""")] // missing schemaVersion
    [InlineData("""{ "schemaVersion": 1 }""")] // missing specValues
    [InlineData("""{ "schemaVersion": 1, "specValues": null }""")] // specValues not object
    [InlineData("""{ "schemaVersion": 1, "specValues": [] }""")] // specValues array
    [InlineData("""{ "schemaVersion": 1, "specValues": {}, "extraEnvelope": true }""")] // unknown envelope prop
    [InlineData("""{ "schemaVersion": 1, "schemaVersion": 2, "specValues": {} }""")] // duplicate envelope
    [InlineData("""{ "schemaVersion": 1, "SchemaVersion": 2, "specValues": {} }""")] // duplicate case envelope
    [InlineData("""{ "schemaVersion": 1, "specValues": { "weight": 100, "weight": 200 } }""")] // duplicate in specValues
    [InlineData("""malformed-json""")]
    [InlineData("")]
    public async Task Validate_MalformedOrDuplicateEnvelope_Returns400ProblemDetails(string invalidBody)
    {
        Authenticate(CustomerToken);
        var eq = await CreateEquipmentTypeWithSchemaAsync("VAL-ENV-01", EquipmentTypeStatus.Published);

        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(invalidBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(CatalogErrorCode.Catalog.InvalidRequest, problem.Title);
    }

    // ========================================================
    // 6. Corrupted Stored Schema: Safe 500 ProblemDetails
    // ========================================================

    [Fact]
    public async Task Validate_CorruptedStoredSchemaDefinition_ReturnsSafe500()
    {
        Authenticate(CustomerToken);
        var eq = await CreateEquipmentTypeWithSchemaAsync("CORRUPT-SCHEMA", EquipmentTypeStatus.Published);

        // Corrupt the schema definition directly in database:
        // Set an invalid definition with an unsupported dataType
        string badJson = "{\"fields\":[{\"key\":\"bad\",\"label\":\"Bad\",\"dataType\":\"unsupported_type\"}]}";
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE \"catalog\".\"EquipmentTypeSpecSchemas\" SET \"Definition\" = {0}::jsonb WHERE \"EquipmentTypeId\" = {1} AND \"Version\" = 1",
            badJson,
            eq.Id);

        string body = """{ "schemaVersion": 1, "specValues": { "bad": "any" } }""";

        var res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(CatalogErrorCode.Catalog.Uncategorized, problem.Title);
        // Ensure exception details are not leaked in detail:
        Assert.Equal(CatalogErrors.Catalog.Uncategorized.Message, problem.Detail);
    }
    [Fact]
    public async Task CrossVersionCorruption_PublishedV1Valid_DraftV2Corrupted_V1Succeeds_V2Returns404()
    {
        var eq = await CreateEquipmentTypeWithSchemaAsync("CORRUPT-DRAFT-V2", EquipmentTypeStatus.Published);

        // Chèn draft v2 hỏng trực tiếp vào PostgreSQL
        string badJson = "{\"fields\":[{\"key\":\"bad\",\"label\":\"Bad\",\"dataType\":\"unsupported_type\"}]}";
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"catalog\".\"EquipmentTypeSpecSchemas\" (\"Id\", \"EquipmentTypeId\", \"Version\", \"Definition\", \"CreatedAt\", \"PublishedAt\") VALUES ({0}, {1}, 2, {2}::jsonb, now(), null)",
            Guid.NewGuid(),
            eq.Id,
            badJson);

        // 1. Current schema (v1) vẫn thành công
        ClearAuthentication();
        var curRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/schema");
        Assert.Equal(HttpStatusCode.OK, curRes.StatusCode);
        var curSchema = await curRes.Content.ReadFromJsonAsync<EquipmentTypePublishedSchemaResponse>();
        Assert.Equal(1, curSchema!.Version);

        // 2. GET /schemas/1 vẫn thành công
        var v1Res = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/schemas/1");
        Assert.Equal(HttpStatusCode.OK, v1Res.StatusCode);

        // 3. POST /validate v1 vẫn thành công
        Authenticate(CustomerToken);
        string v1ValidBody = """{ "schemaVersion": 1, "specValues": { "resolution": "4K", "weight": 500 } }""";
        var val1Res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(v1ValidBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, val1Res.StatusCode);
        var val1Result = await val1Res.Content.ReadFromJsonAsync<SpecificationValidationResult>();
        Assert.True(val1Result!.Valid);

        // 4. Yêu cầu draft v2 trả 404 (database lọc publishedOnly=true, không cố deserialize JSON hỏng)
        ClearAuthentication();
        var v2Res = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/schemas/2");
        Assert.Equal(HttpStatusCode.NotFound, v2Res.StatusCode);
        var p2 = await v2Res.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.SchemaNotFound, p2!.Title);

        Authenticate(CustomerToken);
        string v2Body = """{ "schemaVersion": 2, "specValues": { "bad": "any" } }""";
        var val2Res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(v2Body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, val2Res.StatusCode);
        var val2P = await val2Res.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.SchemaNotFound, val2P!.Title);
    }

    [Fact]
    public async Task CrossVersionCorruption_PublishedV2Valid_HistoricalV1Corrupted_V2Succeeds_V1Returns500()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeWithSchemaAsync("CORRUPT-HIST-V1", EquipmentTypeStatus.Published);

        // Publish v2
        string v2Body = """
            {
                "fields": [
                    { "key": "f2", "label": "Field 2", "dataType": "text", "required": true }
                ]
            }
            """;
        var createDraft = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(v2Body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, createDraft.StatusCode);

        var pubV2 = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/2/publish",
            null);
        Assert.Equal(HttpStatusCode.OK, pubV2.StatusCode);

        // Làm hỏng lịch sử v1 trực tiếp trong DB
        string badJson = "{\"fields\":[{\"key\":\"bad\",\"label\":\"Bad\",\"dataType\":\"unsupported_type\"}]}";
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE \"catalog\".\"EquipmentTypeSpecSchemas\" SET \"Definition\" = {0}::jsonb WHERE \"EquipmentTypeId\" = {1} AND \"Version\" = 1",
            badJson,
            eq.Id);

        // 1. Current schema (v2) vẫn thành công
        ClearAuthentication();
        var curRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/schema");
        Assert.Equal(HttpStatusCode.OK, curRes.StatusCode);
        var curSchema = await curRes.Content.ReadFromJsonAsync<EquipmentTypePublishedSchemaResponse>();
        Assert.Equal(2, curSchema!.Version);

        // 2. GET /schemas/2 vẫn thành công
        var v2Res = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/schemas/2");
        Assert.Equal(HttpStatusCode.OK, v2Res.StatusCode);

        // 3. POST /validate v2 vẫn thành công
        Authenticate(CustomerToken);
        string v2ValBody = """{ "schemaVersion": 2, "specValues": { "f2": "valid" } }""";
        var val2Res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(v2ValBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, val2Res.StatusCode);
        var val2Result = await val2Res.Content.ReadFromJsonAsync<SpecificationValidationResult>();
        Assert.True(val2Result!.Valid);

        // 4. GET /schemas/1 trả safe 500
        ClearAuthentication();
        var v1Res = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/schemas/1");
        Assert.Equal(HttpStatusCode.InternalServerError, v1Res.StatusCode);
        var p1 = await v1Res.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.Catalog.Uncategorized, p1!.Title);

        // 5. POST /validate v1 trả safe 500, không lộ exception
        Authenticate(CustomerToken);
        string v1ValBody = """{ "schemaVersion": 1, "specValues": { "resolution": "4K" } }""";
        var val1Res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(v1ValBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.InternalServerError, val1Res.StatusCode);
        var val1P = await val1Res.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.Catalog.Uncategorized, val1P!.Title);
        Assert.Equal(CatalogErrors.Catalog.Uncategorized.Message, val1P.Detail);
    }

    [Fact]
    public async Task Validate_NonExistentType_OrNonExistentVersion_Returns404_NoFallback()
    {
        Authenticate(CustomerToken);
        var eq = await CreateEquipmentTypeWithSchemaAsync("NO-FALLBACK-TEST", EquipmentTypeStatus.Published);

        // 1. Type không tồn tại -> 404 EquipmentType.NotFound
        string body = """{ "schemaVersion": 1, "specValues": { "resolution": "4K", "weight": 500 } }""";
        var resNoType = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{Guid.NewGuid()}/specifications/validate",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, resNoType.StatusCode);
        var pNoType = await resNoType.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.NotFound, pNoType!.Title);

        // 2. Version không tồn tại trên type tồn tại -> 404 EquipmentType.SchemaNotFound (không fallback sang current version 1)
        string bodyVer999 = """{ "schemaVersion": 999, "specValues": { "resolution": "4K", "weight": 500 } }""";
        var resNoVer = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(bodyVer999, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, resNoVer.StatusCode);
        var pNoVer = await resNoVer.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.SchemaNotFound, pNoVer!.Title);
    }

    // ========================================================
    // 7. Lifecycle: Multi-version Immutability and Edit/Publish Conflict
    // ========================================================

    [Fact]
    public async Task Lifecycle_PublishV2_PreservesV1SchemaAndValidation_EditPublishedReturnsConflict()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeWithSchemaAsync("LIFECYCLE-V1V2", EquipmentTypeStatus.Published);

        // 1. Create and Publish V2 with different definition (weight changed to min 10, max 100, unit "kg")
        string v2Body = """
            {
                "fields": [
                    { "key": "weight", "label": "Khối lượng", "dataType": "number", "required": true, "primary": true, "displayOrder": 1, "min": 10, "max": 100, "unit": "kg" }
                ]
            }
            """;

        var createDraftRes = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
            new StringContent(v2Body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, createDraftRes.StatusCode);

        var pubDraftRes = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/2/publish",
            null);
        Assert.Equal(HttpStatusCode.OK, pubDraftRes.StatusCode);

        // 2. Validate against V1: still validates against V1 definition (requires resolution, weight bounds 100-5000)
        string testPayloadV1 = """{ "schemaVersion": 1, "specValues": { "resolution": "4K", "weight": 500 } }""";
        var val1Res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(testPayloadV1, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, val1Res.StatusCode);
        var v1Result = await val1Res.Content.ReadFromJsonAsync<SpecificationValidationResult>();
        Assert.True(v1Result!.Valid);

        // In V1, weight = 50 is OUT_OF_RANGE (min is 100)
        string testPayloadV1Invalid = """{ "schemaVersion": 1, "specValues": { "resolution": "4K", "weight": 50 } }""";
        var val1InvRes = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(testPayloadV1Invalid, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, val1InvRes.StatusCode);
        var v1InvResult = await val1InvRes.Content.ReadFromJsonAsync<SpecificationValidationResult>();
        Assert.False(v1InvResult!.Valid);
        Assert.Equal(SpecificationValuesValidator.CodeOutOfRange, v1InvResult.Errors[0].Code);

        // 3. Validate against V2: weight = 50 is VALID (min 10, max 100) and resolution is UNKNOWN_FIELD!
        string testPayloadV2 = """{ "schemaVersion": 2, "specValues": { "weight": 50 } }""";
        var val2Res = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(testPayloadV2, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, val2Res.StatusCode);
        var v2Result = await val2Res.Content.ReadFromJsonAsync<SpecificationValidationResult>();
        Assert.True(v2Result!.Valid);

        // 4. Sửa schema đã publish (PUT v1 hoặc PUT v2) trả 409 Conflict
        var editRes = await client.PutAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1",
            new StringContent(v2Body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, editRes.StatusCode);
        var confProblem = await editRes.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.SchemaAlreadyPublished, confProblem!.Title);
    }

    [Fact]
    public async Task Lifecycle_RaceEditAndPublish_Deterministic_PublishWins_EditGets409()
    {
        var (customFactory, customClient) = CreateConcurrencyTestClient();
        using (customFactory)
        using (customClient)
        {
            var eq = await CreateEquipmentTypeWithSchemaAsync("RACE-PUB-WINS", EquipmentTypeStatus.Published);

            string draftV2 = """{ "fields": [{ "key": "fInitial", "label": "Initial", "dataType": "text", "required": true }] }""";
            var createDraftRes = await customClient.PostAsync(
                $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
                new StringContent(draftV2, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, createDraftRes.StatusCode);

            string editV2 = """{ "fields": [{ "key": "fEdited", "label": "Edited", "dataType": "text", "required": true }] }""";

            var editArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowEditToSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            ConcurrencyTestRepository.BeforeUpdateDraftAsync = async () =>
            {
                editArrived.TrySetResult();
                await allowEditToSave.Task.WaitAsync(TimeSpan.FromSeconds(5));
            };

            ConcurrencyTestRepository.BeforePublishDraftAsync = async () =>
            {
                // Đợi edit cũng đã đọc cùng revision trước khi publish commit
                await editArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            };

            try
            {
                var editTask = customClient.PutAsync(
                    $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/2",
                    new StringContent(editV2, Encoding.UTF8, "application/json"));

                var publishTask = customClient.PostAsync(
                    $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/2/publish",
                    null);

                // Publish thắng và commit trước
                var publishRes = await publishTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(HttpStatusCode.OK, publishRes.StatusCode);

                // Cho phép edit tiếp tục commit với revision cũ
                allowEditToSave.SetResult();

                var editRes = await editTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(HttpStatusCode.Conflict, editRes.StatusCode);
                var problem = await editRes.Content.ReadFromJsonAsync<ProblemDetails>();
                Assert.Equal(CatalogErrorCode.EquipmentType.ConcurrencyConflict, problem!.Title);
            }
            finally
            {
                ConcurrencyTestRepository.BeforeUpdateDraftAsync = null;
                ConcurrencyTestRepository.BeforePublishDraftAsync = null;
            }
        }
    }

    [Fact]
    public async Task Lifecycle_RaceEditAndPublish_Deterministic_EditWins_PublishGets409()
    {
        var (customFactory, customClient) = CreateConcurrencyTestClient();
        using (customFactory)
        using (customClient)
        {
            var eq = await CreateEquipmentTypeWithSchemaAsync("RACE-EDIT-WINS", EquipmentTypeStatus.Published);

            string draftV2 = """{ "fields": [{ "key": "fInitial", "label": "Initial", "dataType": "text", "required": true }] }""";
            var createDraftRes = await customClient.PostAsync(
                $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas",
                new StringContent(draftV2, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, createDraftRes.StatusCode);

            string editV2 = """{ "fields": [{ "key": "fEdited", "label": "Edited", "dataType": "text", "required": true }] }""";

            var publishArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowPublishToSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            ConcurrencyTestRepository.BeforePublishDraftAsync = async () =>
            {
                publishArrived.TrySetResult();
                await allowPublishToSave.Task.WaitAsync(TimeSpan.FromSeconds(5));
            };

            ConcurrencyTestRepository.BeforeUpdateDraftAsync = async () =>
            {
                // Đợi publish cũng đã đọc cùng revision trước khi edit commit
                await publishArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            };

            try
            {
                var publishTask = customClient.PostAsync(
                    $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/2/publish",
                    null);

                var editTask = customClient.PutAsync(
                    $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/2",
                    new StringContent(editV2, Encoding.UTF8, "application/json"));

                // Edit thắng và commit trước
                var editRes = await editTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(HttpStatusCode.OK, editRes.StatusCode);

                // Cho phép publish tiếp tục commit với revision cũ
                allowPublishToSave.SetResult();

                var publishRes = await publishTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(HttpStatusCode.Conflict, publishRes.StatusCode);
                var problem = await publishRes.Content.ReadFromJsonAsync<ProblemDetails>();
                Assert.Equal(CatalogErrorCode.EquipmentType.ConcurrencyConflict, problem!.Title);

                // Reset hooks before retry so retry executes without gates
                ConcurrencyTestRepository.BeforeUpdateDraftAsync = null;
                ConcurrencyTestRepository.BeforePublishDraftAsync = null;

                // Thử publish lại với revision mới: thành công
                var retryPubRes = await customClient.PostAsync(
                    $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/2/publish",
                    null);
                Assert.Equal(HttpStatusCode.OK, retryPubRes.StatusCode);

                // Schema v2 được publish chứa đúng definition đã edit
                var getV2 = await customClient.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/schemas/2");
                Assert.Equal(HttpStatusCode.OK, getV2.StatusCode);
                var v2Schema = await getV2.Content.ReadFromJsonAsync<EquipmentTypePublishedSchemaResponse>();
                Assert.NotNull(v2Schema);
                Assert.Equal("fEdited", v2Schema.Fields[0].Key);
            }
            finally
            {
                ConcurrencyTestRepository.BeforeUpdateDraftAsync = null;
                ConcurrencyTestRepository.BeforePublishDraftAsync = null;
            }
        }
    }

    [Fact]
    public async Task Lifecycle_Immutability_AfterPublish_EditRejected_DefinitionAndRulesUnchanged()
    {
        Authenticate(AdminToken);
        var eq = await CreateEquipmentTypeWithSchemaAsync("IMMUTABLE-PROVE", EquipmentTypeStatus.Published);

        // 1. Lưu lại toàn bộ definition của schema 1 đã publish
        var getInitialRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/schemas/1");
        Assert.Equal(HttpStatusCode.OK, getInitialRes.StatusCode);
        var initialSchema = await getInitialRes.Content.ReadFromJsonAsync<EquipmentTypePublishedSchemaResponse>();
        Assert.NotNull(initialSchema);
        string initialDefinitionJson = JsonSerializer.Serialize(initialSchema.Fields);

        // 2. Thử sửa schema 1 đã publish
        string attemptedEditBody = """
            {
                "fields": [
                    { "key": "compromisedField", "label": "Compromised", "dataType": "text", "required": false }
                ]
            }
            """;
        var editRes = await client.PutAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/spec-schemas/1",
            new StringContent(attemptedEditBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, editRes.StatusCode);
        var conflictProblem = await editRes.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(CatalogErrorCode.EquipmentType.SchemaAlreadyPublished, conflictProblem!.Title);

        // 3. Đọc lại schema 1 và so sánh: definition hoàn toàn bất biến
        var getAfterRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}/schemas/1");
        Assert.Equal(HttpStatusCode.OK, getAfterRes.StatusCode);
        var afterSchema = await getAfterRes.Content.ReadFromJsonAsync<EquipmentTypePublishedSchemaResponse>();
        Assert.NotNull(afterSchema);
        string afterDefinitionJson = JsonSerializer.Serialize(afterSchema.Fields);
        Assert.Equal(initialDefinitionJson, afterDefinitionJson);

        // 4. Validate specification: chứng minh rules không bị đổi
        // Payload hợp lệ theo definition ban đầu (resolution, weight 100-5000) thành công
        string validInitialPayload = """{ "schemaVersion": 1, "specValues": { "resolution": "1080p", "weight": 500 } }""";
        var valSuccessRes = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(validInitialPayload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, valSuccessRes.StatusCode);
        var valSuccess = await valSuccessRes.Content.ReadFromJsonAsync<SpecificationValidationResult>();
        Assert.True(valSuccess!.Valid);

        // Payload chỉ hợp lệ theo bản edit bị chặn (chỉ có compromisedField) thất bại với rules ban đầu
        string attemptedEditPayload = """{ "schemaVersion": 1, "specValues": { "compromisedField": "test" } }""";
        var valFailRes = await client.PostAsync(
            $"/api/v1/catalog/equipment-types/{eq.Id}/specifications/validate",
            new StringContent(attemptedEditPayload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, valFailRes.StatusCode);
        var valFail = await valFailRes.Content.ReadFromJsonAsync<SpecificationValidationResult>();
        Assert.False(valFail!.Valid);
        Assert.Contains(valFail.Errors, e => e.Field == "resolution" && e.Code == SpecificationValuesValidator.CodeRequired);
        Assert.Contains(valFail.Errors, e => e.Field == "weight" && e.Code == SpecificationValuesValidator.CodeRequired);
        Assert.Contains(valFail.Errors, e => e.Field == "compromisedField" && e.Code == SpecificationValuesValidator.CodeUnknownField);
    }

    private (WebApplicationFactory<Program> CustomFactory, HttpClient CustomClient) CreateConcurrencyTestClient()
    {
        var customFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IEquipmentTypeRepository));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }
                services.AddScoped<IEquipmentTypeRepository>(sp =>
                {
                    var db = sp.GetRequiredService<CatalogServiceDbContext>();
                    var realRepo = new EquipmentTypeRepository(db);
                    return new ConcurrencyTestRepository(realRepo);
                });
            });
        });

        var customClient = customFactory.CreateClient();
        customClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return (customFactory, customClient);
    }

    private sealed class ConcurrencyTestRepository(IEquipmentTypeRepository inner) : IEquipmentTypeRepository
    {
        public static Func<Task>? BeforeUpdateDraftAsync { get; set; }
        public static Func<Task>? BeforePublishDraftAsync { get; set; }

        public Task<PagedResult<EquipmentTypeListItemDto>> SearchPublishedAsync(
            GetEquipmentTypesRequest request,
            IReadOnlyDictionary<Guid, int> listingCounts,
            CancellationToken cancellationToken = default) =>
            inner.SearchPublishedAsync(request, listingCounts, cancellationToken);

        public Task<EquipmentTypeDetailDto?> GetPublishedByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            inner.GetPublishedByIdAsync(id, cancellationToken);

        public Task<EquipmentType?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<EquipmentTypeDetailDto?> GetDetailByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            inner.GetDetailByIdAsync(id, cancellationToken);

        public Task<EquipmentTypeMetadataDto?> GetMetadataByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            inner.GetMetadataByIdAsync(id, cancellationToken);

        public Task<bool> ExistsByCodeAsync(
            string code,
            Guid? excludeId = null,
            CancellationToken cancellationToken = default) =>
            inner.ExistsByCodeAsync(code, excludeId, cancellationToken);

        public Task<Result<EquipmentTypeDetailDto>> AddAsync(
            EquipmentType equipmentType,
            IReadOnlyList<EquipmentTypeMediaItemDto?>? mediaItems,
            EquipmentTypeSpecDefinition? initialSpecSchema = null,
            CancellationToken cancellationToken = default) =>
            inner.AddAsync(equipmentType, mediaItems, initialSpecSchema, cancellationToken);

        public Task<Result<EquipmentTypeDetailDto>> SaveAsync(
            EquipmentType entity,
            IReadOnlyList<EquipmentTypeMediaItemDto?>? desiredMedia,
            CancellationToken cancellationToken = default) =>
            inner.SaveAsync(entity, desiredMedia, cancellationToken);

        public Task<Result<EquipmentTypeDetailDto>> SaveStateTransitionAsync(
            EquipmentType entity,
            CancellationToken cancellationToken = default) =>
            inner.SaveStateTransitionAsync(entity, cancellationToken);

        public Task<IReadOnlyList<EquipmentTypeSpecSchemaDto>> GetSpecSchemasAsync(
            Guid equipmentTypeId,
            CancellationToken cancellationToken = default) =>
            inner.GetSpecSchemasAsync(equipmentTypeId, cancellationToken);

        public Task<EquipmentTypeSpecSchemaDto?> GetSpecSchemaAsync(
            Guid equipmentTypeId,
            int version,
            CancellationToken cancellationToken = default) =>
            inner.GetSpecSchemaAsync(equipmentTypeId, version, cancellationToken);

        public Task<EquipmentTypeSpecSchemaDto?> GetSpecSchemaAsync(
            Guid equipmentTypeId,
            int version,
            bool publishedOnly,
            CancellationToken cancellationToken = default) =>
            inner.GetSpecSchemaAsync(equipmentTypeId, version, publishedOnly, cancellationToken);

        public Task<EquipmentTypeSpecSchema?> GetDraftSpecSchemaEntityAsync(
            Guid equipmentTypeId,
            CancellationToken cancellationToken = default) =>
            inner.GetDraftSpecSchemaEntityAsync(equipmentTypeId, cancellationToken);

        public Task<EquipmentTypeSpecSchema?> GetSpecSchemaEntityAsync(
            Guid equipmentTypeId,
            int version,
            CancellationToken cancellationToken = default) =>
            inner.GetSpecSchemaEntityAsync(equipmentTypeId, version, cancellationToken);

        public Task<int> GetMaxSpecSchemaVersionAsync(
            Guid equipmentTypeId,
            CancellationToken cancellationToken = default) =>
            inner.GetMaxSpecSchemaVersionAsync(equipmentTypeId, cancellationToken);

        public Task<Result<EquipmentTypeSpecSchemaDto>> AddSpecSchemaDraftAsync(
            EquipmentType parent,
            EquipmentTypeSpecDefinition definition,
            CancellationToken cancellationToken = default) =>
            inner.AddSpecSchemaDraftAsync(parent, definition, cancellationToken);

        public async Task<Result<EquipmentTypeSpecSchemaDto>> UpdateSpecSchemaDraftAsync(
            EquipmentType parent,
            EquipmentTypeSpecSchema draftSchema,
            EquipmentTypeSpecDefinition newDefinition,
            CancellationToken cancellationToken = default)
        {
            if (BeforeUpdateDraftAsync != null)
            {
                await BeforeUpdateDraftAsync();
            }
            return await inner.UpdateSpecSchemaDraftAsync(parent, draftSchema, newDefinition, cancellationToken);
        }

        public async Task<Result<EquipmentTypeSpecSchemaDto>> PublishSpecSchemaAsync(
            EquipmentType parent,
            EquipmentTypeSpecSchema draftSchema,
            CancellationToken cancellationToken = default)
        {
            if (BeforePublishDraftAsync != null)
            {
                await BeforePublishDraftAsync();
            }
            return await inner.PublishSpecSchemaAsync(parent, draftSchema, cancellationToken);
        }
    }

    // ========================================================
    // 8. Detail Response: primarySpecs and Regression Checks
    // ========================================================

    [Fact]
    public async Task Detail_ReturnsPrimarySpecs_AndSearchListDoesNotContainSchemaOrPrimarySpecs()
    {
        ClearAuthentication();
        var eq = await CreateEquipmentTypeWithSchemaAsync("PRIMARY-SPEC-TEST", EquipmentTypeStatus.Published);

        // 1. GET Detail
        var detailRes = await client.GetAsync($"/api/v1/catalog/equipment-types/{eq.Id}");
        Assert.Equal(HttpStatusCode.OK, detailRes.StatusCode);

        string detailJson = await detailRes.Content.ReadAsStringAsync();
        using var detailDoc = JsonDocument.Parse(detailJson);
        var root = detailDoc.RootElement;

        // Verify primarySpecs is present and correctly structured
        Assert.True(root.TryGetProperty("primarySpecs", out var primarySpecsElem));
        Assert.Equal(JsonValueKind.Array, primarySpecsElem.ValueKind);
        Assert.Equal(2, primarySpecsElem.GetArrayLength());

        // Sorted by DisplayOrder, then Key:
        // weight (DisplayOrder: 1, Primary: true, Unit: "g")
        // resolution (DisplayOrder: 2, Primary: true, Unit: null)
        var item0 = primarySpecsElem[0];
        Assert.Equal("weight", item0.GetProperty("key").GetString());
        Assert.Equal("Khối lượng", item0.GetProperty("label").GetString());
        Assert.Equal("g", item0.GetProperty("unit").GetString());

        var item1 = primarySpecsElem[1];
        Assert.Equal("resolution", item1.GetProperty("key").GetString());
        Assert.Equal("Độ phân giải", item1.GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, item1.GetProperty("unit").ValueKind);

        // Verify specSchema and currentSchemaVersion are preserved
        Assert.True(root.TryGetProperty("currentSchemaVersion", out var curVerElem));
        Assert.Equal(1, curVerElem.GetInt32());
        Assert.True(root.TryGetProperty("specSchema", out var specSchemaElem));
        Assert.Equal(JsonValueKind.Object, specSchemaElem.ValueKind);

        // 2. Search List
        var searchRes = await client.GetAsync("/api/v1/catalog/equipment-types");
        Assert.Equal(HttpStatusCode.OK, searchRes.StatusCode);

        string searchJson = await searchRes.Content.ReadAsStringAsync();
        using var searchDoc = JsonDocument.Parse(searchJson);
        var searchItems = searchDoc.RootElement.GetProperty("items");

        foreach (var item in searchItems.EnumerateArray())
        {
            Assert.False(item.TryGetProperty("primarySpecs", out _));
            Assert.False(item.TryGetProperty("specSchema", out _));
        }
    }
}
