using System.Net;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.TestHost;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.CatalogService.Api.Observability;
using Dprental.CatalogService.Api.Security;
using Dprental.CatalogService.Application.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class StartupAndConfigurationTests
{
    [Fact]
    public void Jwt_ValidOptions_Pass()
    {
        Assert.True(new JwtOptionsValidator().Validate(null, ValidJwtOptions()).Succeeded);
    }

    [Fact]
    public void Jwt_MissingIssuer_Fails()
    {
        JwtOptions options = ValidJwtOptions();
        options.Issuer = "";

        Assert.True(new JwtOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void Jwt_MissingAudience_Fails()
    {
        JwtOptions options = ValidJwtOptions();
        options.Audience = "";

        Assert.True(new JwtOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/.well-known/jwks.json")]
    [InlineData("ftp://auth-service/.well-known/jwks.json")]
    public void Jwt_InvalidJwksUri_Fails(string jwksUri)
    {
        JwtOptions options = ValidJwtOptions();
        options.JwksUri = jwksUri;

        Assert.True(new JwtOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void Jwt_MaxStaleShorterThanCacheDuration_Fails()
    {
        JwtOptions options = ValidJwtOptions();
        options.CacheDurationMinutes = 10;
        options.MaxStaleMinutes = 5;

        Assert.True(new JwtOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void Jwt_MissingJwksUri_FailsStartup()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseSetting("Auth:Jwt:JwksUri", "");
            });

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Telemetry_InvalidSamplingRatio_Fails(double ratio)
    {
        TelemetryOptionsValidator validator = new();
        TelemetryOptions options = new()
        {
            Tracing = new TracingOptions { SamplingRatio = ratio }
        };

        ValidateOptionsResult result = validator.Validate(null, options);
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("http://collector:4318/invalid-path")]
    [InlineData("ftp://collector:4318/v1/traces")]
    public void Telemetry_InvalidOtlpEndpoint_Fails(string endpoint)
    {
        TelemetryOptionsValidator validator = new();
        TelemetryOptions options = new()
        {
            Otlp = new OtlpOptions { TracesEndpoint = endpoint }
        };

        ValidateOptionsResult result = validator.Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void ForwardedHeaders_EnabledWithEmptyNetworks_FailsStartup()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseTestJwks();

                builder.UseSetting("Auth:ForwardedHeaders:Enabled", "true");
            });

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public void ForwardedHeaders_EnabledWithInvalidCidr_FailsStartup()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseTestJwks();

                builder.UseSetting("Auth:ForwardedHeaders:Enabled", "true");
                builder.UseSetting("Auth:ForwardedHeaders:KnownNetworks:0", "999.999.999.999/24");
            });

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task SwaggerDocument_PublishesBearerSecurityScheme()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(WebHostDefaults.EnvironmentKey, Environments.Development);
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseTestJwks();
            });

        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement securitySchemes = doc.RootElement
            .GetProperty("components")
            .GetProperty("securitySchemes");

        Assert.True(securitySchemes.TryGetProperty("Bearer", out JsonElement bearerScheme));
        Assert.Equal("http", bearerScheme.GetProperty("type").GetString());
        Assert.Equal("bearer", bearerScheme.GetProperty("scheme").GetString());

        // Thiếu security requirement trên operation thì Swagger UI không gửi token dù đã Authorize.
        JsonElement equipmentTypes = doc.RootElement.GetProperty("paths").GetProperty("/api/v1/catalog/equipment-types");
        JsonElement postSecurity = equipmentTypes.GetProperty("post").GetProperty("security");
        Assert.Contains(postSecurity.EnumerateArray(), requirement => requirement.TryGetProperty("Bearer", out _));
        Assert.False(
            equipmentTypes.GetProperty("get").TryGetProperty("security", out JsonElement getSecurity)
            && getSecurity.GetArrayLength() > 0);
    }

    [Fact]
    public async Task SwaggerDocument_ContainsSixEquipmentTypeEndpoints_AndNoLegacyEquipments()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(WebHostDefaults.EnvironmentKey, Environments.Development);
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseTestJwks();
            });

        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);

        JsonElement paths = doc.RootElement.GetProperty("paths");
        int equipmentTypeEndpointsCount = 0;
        foreach (var path in paths.EnumerateObject())
        {
            if (path.Name.Contains("equipment-types", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var op in path.Value.EnumerateObject())
                {
                    equipmentTypeEndpointsCount++;
                }
            }
        }
        Assert.Equal(15, equipmentTypeEndpointsCount);
        Assert.DoesNotContain("/api/v1/catalog/equipments", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dailyPrice", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("regionId", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/v1/catalog/equipment-types/{equipmentTypeId}/schema", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/v1/catalog/equipment-types/{equipmentTypeId}/schemas/{version}", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/v1/catalog/equipment-types/{equipmentTypeId}/specifications/validate", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/v1/catalog/equipment-types/{id}/schemas/batch", json, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task SwaggerDocument_AssertsExactPatchContract_AndSerializesSuccessfully()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(WebHostDefaults.EnvironmentKey, Environments.Development);
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseTestJwks();
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IEquipmentTypeRepository>();
                    services.AddScoped<IEquipmentTypeRepository, DummyEmptyEquipmentTypeRepository>();
                });
            });

        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);

        JsonElement schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("UpdateEquipmentTypeRequest", out JsonElement patchSchema));

        JsonElement properties = patchSchema.GetProperty("properties");
        var propertyNames = properties.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(5, propertyNames.Count);
        Assert.Contains("code", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("name", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("description", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("categoryIds", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("media", propertyNames, StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("isCodeSpecified", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("isNameSpecified", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("isDescriptionSpecified", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("isCategoryIdsSpecified", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("isMediaSpecified", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("hasAnyFieldSpecified", propertyNames, StringComparer.OrdinalIgnoreCase);

        foreach (var prop in properties.EnumerateObject())
        {
            bool isNullable = prop.Value.TryGetProperty("nullable", out JsonElement nullProp) && nullProp.GetBoolean();
            if (string.Equals(prop.Name, "description", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(isNullable, "description phải nullable");
            }
            else
            {
                Assert.False(isNullable, $"{prop.Name} không được nullable");
            }
        }

        if (patchSchema.TryGetProperty("required", out JsonElement reqProp))
        {
            Assert.Equal(0, reqProp.GetArrayLength());
        }

        var validPayload = new
        {
            name = "Swagger Valid Name",
            description = (string?)null
        };
        string payloadJson = JsonSerializer.Serialize(validPayload);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestJwtHelper.GenerateToken(sub: Guid.NewGuid().ToString(), sid: Guid.NewGuid().ToString(), role: "adm"));
        using HttpResponseMessage patchRes = await client.PatchAsync(
            $"/api/v1/catalog/equipment-types/{Guid.NewGuid()}",
            new StringContent(payloadJson, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, patchRes.StatusCode);
    }

    [Fact]
    public async Task Cors_Development_AllowsLocalhost3000()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(WebHostDefaults.EnvironmentKey, Environments.Development);
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseTestJwks();
            });

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Options, "/api/v1/catalog/equipment-types");
        request.Headers.Add("Origin", "http://localhost:3000");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("http://localhost:3000", response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
    }

    [Fact]
    public async Task Cors_Production_DoesNotAllowLocalhost3000_UnlessConfigured()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(WebHostDefaults.EnvironmentKey, Environments.Production);
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseTestJwks();
            });

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Options, "/api/v1/catalog/equipment-types");
        request.Headers.Add("Origin", "http://localhost:3000");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
    private static JwtOptions ValidJwtOptions() => new()
    {
        Issuer = "dprental-authservice",
        Audience = "dprental-clients",
        JwksUri = TestJwks.Uri
    };

    private sealed class DummyEmptyEquipmentTypeRepository : IEquipmentTypeRepository
    {
        public Task<PagedResult<EquipmentTypeListItemDto>> SearchPublishedAsync(GetEquipmentTypesRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<EquipmentTypeListItemDto>([], 1, 20, 0));

        public Task<EquipmentTypeDetailDto?> GetPublishedByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentTypeDetailDto?>(null);

        public Task<EquipmentType?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentType?>(null);

        public Task<EquipmentTypeDetailDto?> GetDetailByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentTypeDetailDto?>(null);

        public Task<bool> ExistsByCodeAsync(string code, Guid? excludeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Result<EquipmentTypeDetailDto>> AddAsync(EquipmentType equipmentType, IReadOnlyList<EquipmentTypeMediaItemDto?>? mediaItems, EquipmentTypeSpecDefinition? initialSpecSchema = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result<EquipmentTypeDetailDto>> SaveAsync(EquipmentType entity, IReadOnlyList<EquipmentTypeMediaItemDto?>? desiredMedia, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result<EquipmentTypeDetailDto>> SaveStateTransitionAsync(EquipmentType entity, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<EquipmentTypeSpecSchemaDto>> GetSpecSchemasAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EquipmentTypeSpecSchemaDto>>([]);

        public Task<EquipmentTypeSpecSchemaDto?> GetSpecSchemaAsync(Guid equipmentTypeId, int version, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentTypeSpecSchemaDto?>(null);

        public Task<EquipmentTypeSpecSchema?> GetDraftSpecSchemaEntityAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentTypeSpecSchema?>(null);

        public Task<EquipmentTypeSpecSchema?> GetSpecSchemaEntityAsync(Guid equipmentTypeId, int version, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentTypeSpecSchema?>(null);

        public Task<int> GetMaxSpecSchemaVersionAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<Result<EquipmentTypeSpecSchemaDto>> AddSpecSchemaDraftAsync(EquipmentType parent, EquipmentTypeSpecDefinition definition, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result<EquipmentTypeSpecSchemaDto>> UpdateSpecSchemaDraftAsync(EquipmentType parent, EquipmentTypeSpecSchema draftSchema, EquipmentTypeSpecDefinition newDefinition, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result<EquipmentTypeSpecSchemaDto>> PublishSpecSchemaAsync(EquipmentType parent, EquipmentTypeSpecSchema draftSchema, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
