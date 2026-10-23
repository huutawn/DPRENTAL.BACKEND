using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.AnalyticsService.Domain.Repositories;

/// <summary>
/// Đăng ký PostgreSQL persistence cho service.
/// </summary>
public static class DomainRepositoriesServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký DbContext sau khi xác thực cấu hình kết nối.
    /// </summary>
    public static IServiceCollection AddAnalyticsServiceDomainRepositories(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres.");
        }

        services.AddDbContext<AnalyticsServiceDbContext>(options => options.UseNpgsql(connectionString));

        return services;
    }
}
