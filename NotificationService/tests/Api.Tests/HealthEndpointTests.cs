using System.Net;
using Dprental.NotificationService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.NotificationService.Api.Tests;

[Trait("Category", "Unit")]
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
                    services.RemoveAll<DbContextOptions<NotificationServiceDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<NotificationServiceDbContext>>();
                    services.AddDbContext<NotificationServiceDbContext>(options =>
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
                builder.UseSetting("ConnectionStrings:Redis", "127.0.0.1:26379");
                builder.UseSetting("Kafka:BootstrapServers", "localhost:9092");
                builder.UseSetting("Kafka:Topics", "rental.events.v1,payment.analytics.v1");
                builder.UseSetting("Services:AuthService:BaseUrl", "http://auth-service:8080");
                builder.UseSetting("InternalAuthentication:ApiKey", "test-internal-api-key");
            });

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public void MissingRedisConnection_FailsAtStartup()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("ConnectionStrings:Redis", "");
                builder.UseSetting("Kafka:BootstrapServers", "localhost:9092");
                builder.UseSetting("Kafka:Topics", "rental.events.v1,payment.analytics.v1");
                builder.UseSetting("Services:AuthService:BaseUrl", "http://auth-service:8080");
                builder.UseSetting("InternalAuthentication:ApiKey", "test-internal-api-key");
            });

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("ConnectionStrings:Redis", "127.0.0.1:26379");
                builder.UseSetting("Kafka:BootstrapServers", "localhost:9092");
                builder.UseSetting("Kafka:Topics", "rental.events.v1,payment.analytics.v1");
                builder.UseSetting("Services:AuthService:BaseUrl", "http://auth-service:8080");
                builder.UseSetting("InternalAuthentication:ApiKey", "test-internal-api-key");
            });
}
