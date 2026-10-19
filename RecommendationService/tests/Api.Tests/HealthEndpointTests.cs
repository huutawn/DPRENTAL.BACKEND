using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Net;
using Dprental.RecommendationService.Domain.DependencyInjection;
using Dprental.RecommendationService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests;

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
                    services.RemoveAll<DbContextOptions<RecommendationServiceDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<RecommendationServiceDbContext>>();
                    services.AddDbContext<RecommendationServiceDbContext>(options =>
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
    public async Task HealthReady_Returns503_WhenDatabaseFails_AndLivenessReturns200()
    {
        using WebApplicationFactory<Program> factory = CreateFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<RecommendationServiceDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<RecommendationServiceDbContext>>();
                    services.AddDbContext<RecommendationServiceDbContext>(options =>
                        options.UseNpgsql("Host=localhost;Database=test;Username=test;Password=test")
                            .AddInterceptors(new FailingConnectionInterceptor()));
                });
            });

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage readyResponse = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readyResponse.StatusCode);

        using HttpResponseMessage liveResponse = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);
        string liveContent = await liveResponse.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", liveContent);
    }

    [Fact]
    public void MissingPostgresConnection_FailsAtStartup()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", ""));

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public void WhitespacePostgresConnection_FailsAtStartup()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", "   "));

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public void AddRecommendationServiceDomainRepositories_RegistersScopedDbContextWithNpgsqlProvider()
    {
        ServiceCollection services = new();
        services.AddRecommendationServiceDomainRepositories("Host=localhost;Database=test;Username=test;Password=test");

        ServiceDescriptor? descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(RecommendationServiceDbContext));
        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        RecommendationServiceDbContext dbContext = scope.ServiceProvider.GetRequiredService<RecommendationServiceDbContext>();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", dbContext.Database.ProviderName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddRecommendationServiceDomainRepositories_RejectsMissingOrWhitespaceConnectionString(string? connectionString)
    {
        ServiceCollection services = new();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => services.AddRecommendationServiceDomainRepositories(connectionString));
        Assert.Equal("Thiếu ConnectionStrings:Postgres.", exception.Message);
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Kafka:Enabled", "false");
                builder.UseSetting("Valkey:Enabled", "false");
            });
    private sealed class FailingConnectionInterceptor : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Simulated database failure");
        }
    }
}
