using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Dprental.Gateway.Extensions.Proxy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ocelot.DependencyInjection;
using Xunit;

namespace Dprental.Gateway.Tests;

public sealed class GatewayOcelotConfigurationTests
{
    private static readonly string[] ExpectedMergedRouteKeys =
    [
        "analytics-hub-negotiate",
        "analytics-hub-ws",
        "analytics-api",
        "analytics-service-openapi",
        "auth-service-google-provider-callback",
        "auth-service",
        "auth-service-openapi",
        "catalog-service-public-categories",
        "catalog-service-public-category-detail",
        "catalog-service-public-equipment-types",
        "catalog-service-public-equipment-type-detail",
        "catalog-service",
        "catalog-service-openapi",
        "catalog-service-public-brands",
        "catalog-service-public-brand-detail",
        "catalog-service-public-equipment-current-schema",
        "catalog-service-public-equipment-schema-version",
        "catalog-service-public-equipment-spec-schema-version",
        "catalog-service-public-equipment-schemas-batch",
        "inventory-service",
        "inventory-service-openapi",
        "inventory-service-public-equipment-listings",
        "inventory-service-public-equipment-listing-detail",
        "inventory-service-public-listing-availability-check",
        "inventory-service-public-availability-check",
        "inventory-service-public-equipment-listings-statistics",
        "media-service-public-content",
        "media-service",
        "media-service-openapi",
        "notification-service",
        "notification-hub-negotiate",
        "notification-hub-ws",
        "notification-service-internal",
        "notification-service-openapi",
        "payment-hub-negotiate",
        "payment-hub-ws",
        "payment-service-openapi",
        "payment-service-payment",
        "recommendation-service-ready",
        "rental-order-hub-negotiate",
        "rental-order-hub-ws",
        "rental-service",
        "rental-service-openapi",
        "review-public-listings",
        "review-public-suppliers",
        "review-public-summary",
        "review-reviews",
        "review-service-openapi",
    ];

    private sealed class MockWebEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Gateway.Api";
        public string WebRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static IConfiguration LoadMergedOcelotConfiguration(string environmentName)
    {
        string ocelotDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Gateway", "Ocelot", environmentName));
        MockWebEnvironment env = new()
        {
            EnvironmentName = environmentName,
            ContentRootPath = Path.GetDirectoryName(Path.GetDirectoryName(ocelotDir))!
        };

        ConfigurationBuilder builder = new();
        builder.AddGatewayOcelotConfiguration(ocelotDir, env);
        return builder.Build();
    }

    [Fact]
    public void EachMergedIndexMatchesExpectedRouteKeyInDevelopmentAndProduction()
    {
        IConfiguration devConfig = LoadMergedOcelotConfiguration("Development");
        IConfiguration prodConfig = LoadMergedOcelotConfiguration("Production");

        List<IConfigurationSection> devRoutes = devConfig.GetSection("Routes").GetChildren().ToList();
        List<IConfigurationSection> prodRoutes = prodConfig.GetSection("Routes").GetChildren().ToList();

        Assert.Equal(ExpectedMergedRouteKeys.Length, devRoutes.Count);
        Assert.Equal(ExpectedMergedRouteKeys.Length, prodRoutes.Count);

        for (int i = 0; i < ExpectedMergedRouteKeys.Length; i++)
        {
            string expectedKey = ExpectedMergedRouteKeys[i];
            Assert.Equal(expectedKey, devRoutes[i]["Key"]);
            Assert.Equal(expectedKey, prodRoutes[i]["Key"]);
        }
    }

    [Fact]
    public void BothEnvironmentsPreserveRoutesAttributesPathsAndWebSocketSchemes()
    {
        IConfiguration devConfig = LoadMergedOcelotConfiguration("Development");
        IConfiguration prodConfig = LoadMergedOcelotConfiguration("Production");

        List<IConfigurationSection> devRoutes = devConfig.GetSection("Routes").GetChildren().ToList();
        List<IConfigurationSection> prodRoutes = prodConfig.GetSection("Routes").GetChildren().ToList();

        // Check GlobalConfiguration Timeout
        Assert.Equal(15, int.Parse(devConfig["GlobalConfiguration:Timeout"]!, CultureInfo.InvariantCulture));
        Assert.Equal(10, int.Parse(prodConfig["GlobalConfiguration:Timeout"]!, CultureInfo.InvariantCulture));

        for (int i = 0; i < devRoutes.Count; i++)
        {
            IConfigurationSection devRoute = devRoutes[i];
            IConfigurationSection prodRoute = prodRoutes[i];

            Assert.Equal(devRoute["Key"], prodRoute["Key"]);
            Assert.Equal(devRoute["UpstreamPathTemplate"], prodRoute["UpstreamPathTemplate"]);
            Assert.Equal(devRoute["DownstreamPathTemplate"], prodRoute["DownstreamPathTemplate"]);

            // Both must define valid non-empty paths
            Assert.False(string.IsNullOrWhiteSpace(devRoute["UpstreamPathTemplate"]));
            // Realtime SignalR hubs retain "ws" scheme across both environments
            if (devRoute["Key"] is "rental-order-hub-ws" or "analytics-hub-ws" or "payment-hub-ws" or "notification-hub-ws")
            {
                Assert.Equal("ws", devRoute["DownstreamScheme"]);
                Assert.Equal("ws", prodRoute["DownstreamScheme"]);
            }
            else
            {
                Assert.Equal("http", devRoute["DownstreamScheme"]);
                Assert.Equal("http", prodRoute["DownstreamScheme"]);
            }

            // Downstream host & port must be valid
            Assert.False(string.IsNullOrWhiteSpace(devRoute["DownstreamHostAndPorts:0:Host"]));
            Assert.True(int.TryParse(devRoute["DownstreamHostAndPorts:0:Port"], out int devPort) && devPort > 0 && devPort <= 65535);

            Assert.False(string.IsNullOrWhiteSpace(prodRoute["DownstreamHostAndPorts:0:Host"]));
            Assert.True(int.TryParse(prodRoute["DownstreamHostAndPorts:0:Port"], out int prodPort) && prodPort > 0 && prodPort <= 65535);
        }
    }

    [Fact]
    public void RouteOrderDoesNotDependOnFileCreationOrder()
    {
        string source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Gateway/Ocelot/Development"));
        string temporary = Path.Combine(Path.GetTempPath(), "gateway-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (string file in Directory.GetFiles(source, "*.json").OrderDescending(StringComparer.Ordinal))
            {
                File.Copy(file, Path.Combine(temporary, Path.GetFileName(file)));
            }

            ConfigurationBuilder builder = new();
            builder.AddGatewayOcelotConfiguration(temporary, new MockWebEnvironment());
            IConfiguration config = builder.Build();
            Assert.Equal(ExpectedMergedRouteKeys,
                config.GetSection("Routes").GetChildren().Select(route => route["Key"]));
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    public void MissingEnvironmentFolderHaltsStartupWithoutFallback()
    {
        Assert.ThrowsAny<Exception>(() =>
        {
            using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("NonExistentEnvironmentName");
            });
            _ = factory.CreateClient();
        });
    }

    [Fact]
    public void DuplicateRouteKeyHaltsStartup()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            Dictionary<string, string?> overrides = new(StringComparer.Ordinal)
            {
                ["GlobalConfiguration:Timeout"] = "10",
                ["Routes:0:Key"] = "duplicate-key",
                ["Routes:0:UpstreamPathTemplate"] = "/path1",
                ["Routes:0:DownstreamPathTemplate"] = "/path1",
                ["Routes:0:DownstreamScheme"] = "http",
                ["Routes:0:DownstreamHostAndPorts:0:Host"] = "service-a",
                ["Routes:0:DownstreamHostAndPorts:0:Port"] = "8080",

                ["Routes:1:Key"] = "duplicate-key",
                ["Routes:1:UpstreamPathTemplate"] = "/path2",
                ["Routes:1:DownstreamPathTemplate"] = "/path2",
                ["Routes:1:DownstreamScheme"] = "http",
                ["Routes:1:DownstreamHostAndPorts:0:Host"] = "service-b",
                ["Routes:1:DownstreamHostAndPorts:0:Port"] = "8080"
            };

            ConfigurationBuilder builder = new();
            builder.AddInMemoryCollection(overrides);
            IConfiguration config = builder.Build();

            OcelotServiceCollectionExtensions.ValidateOcelotConfiguration(config);
        });
    }

    [Fact]
    public void MissingRouteKeyHaltsStartup()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            Dictionary<string, string?> overrides = new(StringComparer.Ordinal)
            {
                ["GlobalConfiguration:Timeout"] = "10",
                ["Routes:0:UpstreamPathTemplate"] = "/path1",
                ["Routes:0:DownstreamPathTemplate"] = "/path1",
                ["Routes:0:DownstreamScheme"] = "http",
                ["Routes:0:DownstreamHostAndPorts:0:Host"] = "service-a",
                ["Routes:0:DownstreamHostAndPorts:0:Port"] = "8080"
            };

            ConfigurationBuilder builder = new();
            builder.AddInMemoryCollection(overrides);
            IConfiguration config = builder.Build();

            OcelotServiceCollectionExtensions.ValidateOcelotConfiguration(config);
        });
    }

    [Fact]
    public void TestOverridesAndEnvironmentVariablesHaveHigherPriorityThanOcelotJson()
    {
        using WebApplicationFactory<Program> factory = DownstreamGatewayTestHost.CreateFactory(new Uri("http://127.0.0.1:49999"));
        IConfiguration config = (IConfiguration)factory.Services.GetService(typeof(IConfiguration))!;

        // Verify overrides for routes from different services.
        Assert.Equal("127.0.0.1", config["Routes:36:DownstreamHostAndPorts:0:Host"]);
        Assert.Equal("49999", config["Routes:36:DownstreamHostAndPorts:0:Port"]);

        Assert.Equal("127.0.0.1", config["Routes:5:DownstreamHostAndPorts:0:Host"]);
        Assert.Equal("49999", config["Routes:5:DownstreamHostAndPorts:0:Port"]);
    }
}
