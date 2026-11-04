using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.ReviewService.Domain.Repositories;

/// <summary>
/// Đăng ký PostgreSQL persistence và repositories cho service.
/// </summary>
public static class DomainRepositoriesServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký DbContext và các Repositories sau khi xác thực cấu hình kết nối.
    /// </summary>
    public static IServiceCollection AddReviewServiceDomainRepositories(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres.");
        }

        services.AddDbContext<ReviewServiceDbContext>(options => options.UseNpgsql(connectionString));
        services.AddReviewServiceRepositories();

        return services;
    }

    /// <summary>
    /// Đăng ký các repository implementations vào DI container.
    /// </summary>
    public static IServiceCollection AddReviewServiceRepositories(this IServiceCollection services)
    {
        services.AddScoped<IReviewRepository, ReviewRepository>();
        services.AddScoped<IReviewAuditRepository, ReviewAuditRepository>();
        services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();

        return services;
    }
}
