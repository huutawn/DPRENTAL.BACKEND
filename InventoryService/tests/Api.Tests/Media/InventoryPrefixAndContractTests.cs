using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dprental.InventoryService.Api.Controllers;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Api.Routing;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Dprental.InventoryService.Api.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Media;

/// <summary>
/// Kiểm thử hợp đồng prefix route /api/v1/inventory, URL media và fail-fast startup validation.
/// </summary>
public sealed class InventoryPrefixAndContractTests
{
    [Fact]
    public void AllBusinessControllers_UseStandardInventoryRoutePrefix_ViaGlobalConvention()
    {
        Assert.Equal("api/v1/inventory", ApiRoutes.Prefix);

        var convention = new ApiRoutePrefixConvention(ApiRoutes.Prefix);
        var appModel = new ApplicationModel();
        var apiAssembly = typeof(InventoryCalendarController).Assembly;
        var controllerTypes = apiAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        Assert.NotEmpty(controllerTypes);

        foreach (var type in controllerTypes)
        {
            var controllerModel = new ControllerModel(type.GetTypeInfo(), type.GetCustomAttributes().ToList());
            var routeAttr = type.GetCustomAttribute<RouteAttribute>();
            controllerModel.Selectors.Add(new SelectorModel
            {
                AttributeRouteModel = routeAttr is not null ? new AttributeRouteModel(routeAttr) : null
            });
            appModel.Controllers.Add(controllerModel);
        }

        convention.Apply(appModel);

        foreach (var controller in appModel.Controllers)
        {
            foreach (var selector in controller.Selectors)
            {
                Assert.NotNull(selector.AttributeRouteModel);
                Assert.StartsWith("api/v1/inventory", selector.AttributeRouteModel.Template, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task AllBusinessEndpoints_UseStandardInventoryRoutePrefix_AtRuntime()
    {
        await using var factory = new InventorySecurityTestFactory();
        var endpointDataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        var apiAssembly = typeof(InventoryCalendarController).Assembly;
        var actionEndpoints = endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e =>
            {
                var descriptor = e.Metadata.GetMetadata<ControllerActionDescriptor>();
                return descriptor is not null && descriptor.ControllerTypeInfo.Assembly == apiAssembly;
            })
            .ToList();

        Assert.NotEmpty(actionEndpoints);

        foreach (var endpoint in actionEndpoints)
        {
            Assert.StartsWith("api/v1/inventory", endpoint.RoutePattern.RawText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MediaReferenceResponse_Contract_MatchesSpecification()
    {
        var mediaId = Guid.NewGuid();
        var url = $"https://gateway.example/api/v1/media/{mediaId:D}/content";
        var response = new MediaReferenceResponse(mediaId, url);

        var json = JsonSerializer.Serialize(response);
        var node = JsonNode.Parse(json)?.AsObject();

        Assert.NotNull(node);
        Assert.Equal(2, node.Count);
        Assert.Equal(mediaId.ToString("D"), node["id"]?.GetValue<string>());
        Assert.Equal(url, node["url"]?.GetValue<string>());

        // URL media tuyệt đối không bị gắn prefix /inventory
        Assert.DoesNotContain("/inventory/media", url);
        Assert.Contains("/api/v1/media/", url);
    }

    private sealed class InvalidConfigTestFactory(Dictionary<string, string?> configOverrides) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(configOverrides);
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<InventoryServiceDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<InventoryServiceDbContext>>();
                services.AddDbContext<InventoryServiceDbContext>(options =>
                    options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
                services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
            });
        }
    }

    [Fact]
    public void Startup_ThrowsOptionsValidationException_WhenMediaServiceConfigIsMissing()
    {
        // Không cấu hình MediaService
        var overrides = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=test;Username=test;Password=test",
            ["Authentication:Jwt:Issuer"] = "https://auth.test",
            ["Authentication:Jwt:HumanAudience"] = "dprental-clients",
            ["Authentication:Jwt:ServiceAudience"] = "inventory-api",
            ["Authentication:Jwt:JwksUri"] = "http://localhost:5000/.well-known/jwks.json"
        };

        using var factory = new InvalidConfigTestFactory(overrides);

        // ValidateOnStart làm factory khởi tạo host ném OptionsValidationException
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.True(
            ex is OptionsValidationException || ex.InnerException is OptionsValidationException,
            $"Expected OptionsValidationException but got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public void Startup_ThrowsOptionsValidationException_WhenApiKeyIsEmpty()
    {
        // Có section MediaService nhưng ApiKey rỗng
        var overrides = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=test;Username=test;Password=test",
            ["Authentication:Jwt:Issuer"] = "https://auth.test",
            ["Authentication:Jwt:HumanAudience"] = "dprental-clients",
            ["Authentication:Jwt:ServiceAudience"] = "inventory-api",
            ["Authentication:Jwt:JwksUri"] = "http://localhost:5000/.well-known/jwks.json",
            ["MediaService:InternalBaseUrl"] = "http://media-service:8080",
            ["MediaService:PublicGatewayUrl"] = "http://localhost:5050",
            ["MediaService:ApiKey"] = "", // Rỗng
            ["MediaService:TimeoutSeconds"] = "10"
        };

        using var factory = new InvalidConfigTestFactory(overrides);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.True(
            ex is OptionsValidationException || ex.InnerException is OptionsValidationException,
            $"Expected OptionsValidationException but got {ex.GetType().Name}: {ex.Message}");
    }
}
