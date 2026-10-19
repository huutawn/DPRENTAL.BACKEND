using Dprental.RecommendationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.TestHelpers;

public sealed class PostgreSqlTestcontainerFixture : IAsyncLifetime
{
    static PostgreSqlTestcontainerFixture()
    {
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("recommendationservice_test")
        .WithUsername("rec_test_user")
        .WithPassword("rec_test_password_secret_123")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    public RecommendationServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<RecommendationServiceDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new RecommendationServiceDbContext(options);
    }
}
