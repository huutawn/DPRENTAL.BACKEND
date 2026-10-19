using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.RecommendationService.Domain.Repositories;

/// <summary>
/// Đăng ký PostgreSQL persistence cho service.
/// </summary>
public static class DomainRepositoriesServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký DbContext sau khi xác thực cấu hình kết nối.
    /// </summary>
    public static IServiceCollection AddRecommendationServiceDomainRepositories(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres.");
        }

        services.AddDbContext<RecommendationServiceDbContext>(options => options.UseNpgsql(connectionString));

        return services;
    }
}
