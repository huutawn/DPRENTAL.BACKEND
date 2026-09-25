using Dprental.MediaService.Api.Health;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minio;

namespace Dprental.MediaService.Api.Tests;

/// <summary>
/// WebApplicationFactory phục vụ kiểm thử tích hợp các endpoint và lifecycle của MediaService.
/// </summary>
public class MediaServiceTestFactory : WebApplicationFactory<Program>
{
    public IPostgreSqlHealthChecker? PostgreSqlHealthCheckerOverride { get; set; }
    public IMinioClient? MinioClientOverride { get; set; }
    public Dictionary<string, string?> ConfigurationOverrides { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var defaultSettings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=5434;Database=mediaservice;Username=mediaservice;Password=mediasecret",
                ["ObjectStorage:Endpoint"] = "http://127.0.0.1:9000",
                ["ObjectStorage:Bucket"] = "dprental-media",
                ["ObjectStorage:AccessKey"] = "mediaadmin",
                ["ObjectStorage:SecretKey"] = "mediasecretpassword"
            };

            foreach (var (k, v) in ConfigurationOverrides)
            {
                defaultSettings[k] = v;
            }

            config.AddInMemoryCollection(defaultSettings);
        });

        builder.ConfigureServices(services =>
        {
            if (PostgreSqlHealthCheckerOverride != null)
            {
                services.RemoveAll<IPostgreSqlHealthChecker>();
                services.AddSingleton(PostgreSqlHealthCheckerOverride);
            }

            if (MinioClientOverride != null)
            {
                services.RemoveAll<IMinioClient>();
                services.AddSingleton(MinioClientOverride);
            }
        });
    }
}
