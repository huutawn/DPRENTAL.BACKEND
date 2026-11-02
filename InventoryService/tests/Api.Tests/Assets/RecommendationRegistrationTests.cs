using Dprental.InventoryService.Api.Recommendation;
using Dprental.InventoryService.Application.Recommendation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class RecommendationRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registration_UsesRecommendationConfiguration_WhenCacheMultiplexerIsRegistered(bool configured)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:RecommendationRedis"] = configured ? "localhost:6379" : null
            }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        // Resolving the cache connection would use a different Redis instance in deployment.
        services.AddSingleton<IConnectionMultiplexer>(_ =>
            throw new InvalidOperationException("Recommendation must own its connection."));
        services.AddLogging();
        services.AddRecommendationRanking(configuration);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        var reader = provider.GetRequiredService<IRecommendationRankingReader>();
        Assert.IsType<ValkeyRecommendationRankingReader>(reader);
        using var scope = provider.CreateScope();
        Assert.Same(reader, scope.ServiceProvider.GetRequiredService<IRecommendationRankingReader>());
        if (!configured)
        {
            var snapshot = await reader.ReadAsync(null);
            Assert.Equal(RecommendationOutcome.Unavailable, snapshot.Outcome);
        }
    }

    [Fact]
    public void Registration_RejectsInvalidRecommendationConnectionString()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:RecommendationRedis"] = "localhost,connectTimeout=invalid"
            }).Build();
        var services = new ServiceCollection();
        services.AddRecommendationRanking(configuration);
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<IRecommendationRankingReader>());
        Assert.Contains("ConnectionStrings:RecommendationRedis", error.Message);
    }
}
