using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Domain.Persistence;
using Dprental.PaymentService.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dprental.PaymentService.Infrastructure.DependencyInjection;

/// <summary>
/// Đăng ký PostgreSQL persistence và Infrastructure repositories cho PaymentService.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddPaymentInfrastructure(
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
        services.AddScoped<IOutboxRepository, EfOutboxRepository>();
        services.AddScoped<ITransactionJournalRepository, EfTransactionJournalRepository>();

        return services;
    }
}
