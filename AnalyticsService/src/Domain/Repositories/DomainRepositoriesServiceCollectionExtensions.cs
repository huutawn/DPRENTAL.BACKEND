using Dprental.AnalyticsService.Domain.IRepositories;
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

        // Scoped: cùng vòng đời với DbContext, mỗi message Kafka một scope riêng
        services.AddScoped<IRentalFactRepository, RentalFactRepository>();
        services.AddScoped<IFinancialFactRepository, FinancialFactRepository>();
        services.AddScoped<IInboxRepository, InboxRepository>();
        services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();

        // Unit of work chính là DbContext của scope, để repository và SaveChanges dùng chung một DbContext
        services.AddScoped<IAnalyticsUnitOfWork>(provider => provider.GetRequiredService<AnalyticsServiceDbContext>());

        return services;
    }
}
