using Dprental.InventoryService.Application.Recommendation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.InventoryService.Api.Recommendation;

public static class RecommendationServiceCollectionExtensions
{
    public static IServiceCollection AddRecommendationRanking(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<RecommendationMetrics>();
        services.AddSingleton<IRecommendationMetrics>(sp => sp.GetRequiredService<RecommendationMetrics>());
        services.AddSingleton<IRecommendationRankingReader, ValkeyRecommendationRankingReader>();

        return services;
    }
}
