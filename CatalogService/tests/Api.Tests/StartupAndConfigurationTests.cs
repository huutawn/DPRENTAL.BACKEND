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
using Dprental.CatalogService.Application.Security.Jwt;
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
    public void Jwt_MissingIssuer_Throws()
    {
        JwtOptions options = new()
        {
            Issuer = "",
            Audience = "dprental-clients",
            Keys = [new JwtKeyOptions { KeyId = "k1", PublicKeyPem = TestJwtHelper.PublicKeyPem }]
        };

        Assert.Throws<ArgumentException>(() => new RsaJwtKeyRing(options));
    }

    [Fact]
    public void Jwt_MissingAudience_Throws()
    {
        JwtOptions options = new()
        {
            Issuer = "dprental-authservice",
            Audience = "",
            Keys = [new JwtKeyOptions { KeyId = "k1", PublicKeyPem = TestJwtHelper.PublicKeyPem }]
        };

        Assert.Throws<ArgumentException>(() => new RsaJwtKeyRing(options));
    }

    [Fact]
    public void Jwt_EmptyKeysList_Throws()
    {
        JwtOptions options = new()
        {
            Issuer = "dprental-authservice",
            Audience = "dprental-clients",
            Keys = []
        };

        Assert.Throws<ArgumentException>(() => new RsaJwtKeyRing(options));
    }

    [Fact]
    public void Jwt_MissingKeyId_Throws()
    {
        JwtOptions options = new()
        {
            Issuer = "dprental-authservice",
            Audience = "dprental-clients",
            Keys = [new JwtKeyOptions { KeyId = "", PublicKeyPem = TestJwtHelper.PublicKeyPem }]
        };

        Assert.Throws<ArgumentException>(() => new RsaJwtKeyRing(options));
    }

    [Fact]
    public void Jwt_DuplicateKeyId_Throws()
    {
        JwtOptions options = new()
        {
            Issuer = "dprental-authservice",
            Audience = "dprental-clients",
            Keys =
            [
                new JwtKeyOptions { KeyId = "dup-key", PublicKeyPem = TestJwtHelper.PublicKeyPem },
                new JwtKeyOptions { KeyId = "dup-key", PublicKeyPem = TestJwtHelper.PublicKeyPem }
            ]
        };

        Assert.Throws<ArgumentException>(() => new RsaJwtKeyRing(options));
    }

    [Fact]
    public void Jwt_InvalidPemContent_Throws()
    {
        JwtOptions options = new()
        {
            Issuer = "dprental-authservice",
            Audience = "dprental-clients",
            Keys = [new JwtKeyOptions { KeyId = "k1", PublicKeyPem = "-----BEGIN PUBLIC KEY-----\nNOT_VALID_BASE64\n-----END PUBLIC KEY-----" }]
        };

        Assert.Throws<ArgumentException>(() => new RsaJwtKeyRing(options));
    }

    [Fact]
    public void Jwt_PrivateKeyPem_IsRejected()
    {
        JwtOptions options = new()
        {
            Issuer = "dprental-authservice",
            Audience = "dprental-clients",
            Keys = [new JwtKeyOptions { KeyId = "k1", PublicKeyPem = TestJwtHelper.PrivateKeyPem }]
        };

        var ex = Assert.Throws<ArgumentException>(() => new RsaJwtKeyRing(options));
        Assert.Contains("must not contain a private key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Jwt_WeakRsaKey_LessThan2048Bits_Throws()
    {
        using RSA weakRsa = RSA.Create(1024);
        string weakPem = weakRsa.ExportRSAPublicKeyPem();

        JwtOptions options = new()
        {
            Issuer = "dprental-authservice",
            Audience = "dprental-clients",
            Keys = [new JwtKeyOptions { KeyId = "k-weak", PublicKeyPem = weakPem }]
        };

        var ex = Assert.Throws<ArgumentException>(() => new RsaJwtKeyRing(options));
        Assert.Contains("2048 bits", ex.Message, StringComparison.OrdinalIgnoreCase);
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
                builder.UseSetting("Auth:Jwt:Keys:0:KeyId", TestJwtHelper.DefaultKeyId);
                builder.UseSetting("Auth:Jwt:Keys:0:PublicKeyPem", TestJwtHelper.PublicKeyPem);

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
                builder.UseSetting("Auth:Jwt:Keys:0:KeyId", TestJwtHelper.DefaultKeyId);
                builder.UseSetting("Auth:Jwt:Keys:0:PublicKeyPem", TestJwtHelper.PublicKeyPem);

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
                builder.UseSetting("Auth:Jwt:Keys:0:KeyId", TestJwtHelper.DefaultKeyId);
                builder.UseSetting("Auth:Jwt:Keys:0:PublicKeyPem", TestJwtHelper.PublicKeyPem);
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
                builder.UseSetting("Auth:Jwt:Keys:0:KeyId", TestJwtHelper.DefaultKeyId);
                builder.UseSetting("Auth:Jwt:Keys:0:PublicKeyPem", TestJwtHelper.PublicKeyPem);
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

        Assert.Equal(6, equipmentTypeEndpointsCount);
        Assert.DoesNotContain("/api/v1/catalog/equipments", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dailyPrice", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("regionId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("specifications", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organizationId", json, StringComparison.OrdinalIgnoreCase);
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
                builder.UseSetting("Auth:Jwt:Keys:0:KeyId", TestJwtHelper.DefaultKeyId);
                builder.UseSetting("Auth:Jwt:Keys:0:PublicKeyPem", TestJwtHelper.PublicKeyPem);
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
        Assert.Contains("categoryId", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("media", propertyNames, StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("isCodeSpecified", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("isNameSpecified", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("isDescriptionSpecified", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("isCategoryIdSpecified", propertyNames, StringComparer.OrdinalIgnoreCase);
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
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestJwtHelper.GenerateToken(sub: Guid.NewGuid().ToString(), sid: Guid.NewGuid().ToString(), role: "Admin"));
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
                builder.UseSetting("Auth:Jwt:Keys:0:KeyId", TestJwtHelper.DefaultKeyId);
                builder.UseSetting("Auth:Jwt:Keys:0:PublicKeyPem", TestJwtHelper.PublicKeyPem);
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
                builder.UseSetting("Auth:Jwt:Keys:0:KeyId", TestJwtHelper.DefaultKeyId);
                builder.UseSetting("Auth:Jwt:Keys:0:PublicKeyPem", TestJwtHelper.PublicKeyPem);
            });

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Options, "/api/v1/catalog/equipment-types");
        request.Headers.Add("Origin", "http://localhost:3000");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
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

        public Task<Result<EquipmentTypeDetailDto>> AddAsync(EquipmentType equipmentType, IReadOnlyList<EquipmentTypeMediaItemDto?>? mediaItems, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result<EquipmentTypeDetailDto>> SaveAsync(EquipmentType entity, IReadOnlyList<EquipmentTypeMediaItemDto?>? desiredMedia, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result<EquipmentTypeDetailDto>> SaveStateTransitionAsync(EquipmentType entity, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
