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

        // Scoped: cùng vòng đời với DbContext; worker tạo một scope cho mỗi message
        services.AddScoped<IRentalFactRepository, RentalFactRepository>();
        services.AddScoped<ISupplierDashboardQueryRepository, SupplierDashboardQueryRepository>();
        services.AddScoped<IProcessedRentalEventRepository, ProcessedRentalEventRepository>();
        services.AddScoped<IPaymentFinancialEventRepository, PaymentFinancialEventRepository>();
        services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();
        // Unit of work chính là DbContext của scope, để repository và SaveChanges dùng chung một DbContext
        services.AddScoped<IAnalyticsUnitOfWork>(provider => provider.GetRequiredService<AnalyticsServiceDbContext>());

        return services;
    }
}
