using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.__SERVICE_NAME__.Domain.Repositories;

/// <summary>
/// Đăng ký PostgreSQL persistence cho service.
/// </summary>
public static class DomainRepositoriesServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký DbContext sau khi xác thực cấu hình kết nối.
    /// </summary>
    public static IServiceCollection Add__SERVICE_NAME__DomainRepositories(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres.");
        }

        services.AddDbContext<__SERVICE_NAME__DbContext>(options => options.UseNpgsql(connectionString));

        return services;
    }
}
