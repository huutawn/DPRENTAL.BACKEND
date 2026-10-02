using System.Net;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task HealthLive_ReturnsOk()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", content);
    }

    [Fact]
    public async Task HealthReady_ReturnsOk()
    {
        using WebApplicationFactory<Program> factory = CreateFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<CatalogServiceDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<CatalogServiceDbContext>>();
                    services.AddDbContext<CatalogServiceDbContext>(options =>
                        options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
                });
            });

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", content);
    }

    [Fact]
    public void MissingPostgresConnection_FailsAtStartup()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "");
                builder.UseSetting("MediaService:BaseUrl", "http://media.test");
                builder.UseSetting("MediaService:ApiKey", "test-api-key");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseSetting("Auth:Jwt:Keys:0:KeyId", TestJwtHelper.DefaultKeyId);
                builder.UseSetting("Auth:Jwt:Keys:0:PublicKeyPem", TestJwtHelper.PublicKeyPem);
            });

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("MediaService:BaseUrl", "http://media.test");
                builder.UseSetting("MediaService:ApiKey", "test-api-key");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseSetting("Auth:Jwt:Keys:0:KeyId", TestJwtHelper.DefaultKeyId);
                builder.UseSetting("Auth:Jwt:Keys:0:PublicKeyPem", TestJwtHelper.PublicKeyPem);
            });
}
