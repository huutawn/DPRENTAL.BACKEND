using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Domain.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dprental.PaymentService.Domain.Repository;

/// <summary>
/// Đăng ký PostgreSQL persistence và Domain repositories cho PaymentService.
/// </summary>
public static class DomainRepositoriesServiceCollectionExtensions
{
    public static IServiceCollection AddPaymentDomainRepositories(
        this IServiceCollection services,
        string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres.");
        }

        services.AddDbContext<PaymentServiceDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IInvoiceRepository, EfInvoiceRepository>();
        services.AddScoped<ISettlementLedger, EfSettlementLedger>();
        services.AddScoped<IManualRefundRepository, EfManualRefundRepository>();

        return services;
    }
}
