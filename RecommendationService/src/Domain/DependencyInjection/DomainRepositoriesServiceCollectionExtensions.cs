using Dprental.RecommendationService.Domain.IRepositories;
using Dprental.RecommendationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dprental.RecommendationService.Domain.DependencyInjection;

/// <summary>
/// Đăng ký PostgreSQL persistence và repository abstractions cho service.
/// </summary>
public static class DomainRepositoriesServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký DbContext và các repository abstractions sau khi xác thực cấu hình kết nối.
    /// </summary>
    public static IServiceCollection AddRecommendationServiceDomainRepositories(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres.");
        }

        services.AddDbContext<RecommendationServiceDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<IInteractionEventRepository, InteractionEventRepository>();
        services.AddScoped<IUserEquipmentTypeInterestRepository, UserEquipmentTypeInterestRepository>();
        services.AddScoped<IListingPopularityRepository, ListingPopularityRepository>();
        services.AddScoped<IInteractionUnitOfWork, InteractionUnitOfWork>();
        services.AddScoped<IValkeyProjectionRepository, ValkeyProjectionRepository>();

        return services;
    }

    /// <summary>
    /// Đăng ký Valkey (Redis) connection provider.
    /// </summary>
    public static IServiceCollection AddRecommendationServiceValkey(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Valkey is enabled but ConnectionStrings:Redis is missing or empty.");
        }

        services.AddSingleton<IValkeyConnectionProvider>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ValkeyConnectionProvider>>();
            return new ValkeyConnectionProvider(connectionString, logger);
        });

        return services;
    }
}
