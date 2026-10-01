using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Dprental.CatalogService.Domain.IRepositories;

namespace Dprental.CatalogService.Domain.Repositories;

/// <summary>
/// Đăng ký PostgreSQL persistence cho service.
/// </summary>
public static class DomainRepositoriesServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký DbContext sau khi xác thực cấu hình kết nối.
    /// </summary>
    public static IServiceCollection AddCatalogServiceDomainRepositories(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres.");
        }

        services.AddDbContext<CatalogServiceDbContext>(options => options.UseNpgsql(
            connectionString,
            npgsqlOptions => npgsqlOptions.MigrationsHistoryTable(
                CatalogServiceDbContext.MigrationsHistoryTableName,
                CatalogServiceDbContext.SchemaName)));
        services.AddScoped<IEquipmentQueryRepository, CatalogRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        return services;
    }
}
