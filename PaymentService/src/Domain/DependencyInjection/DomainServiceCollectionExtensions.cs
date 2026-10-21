using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dprental.PaymentService.Domain.DependencyInjection;

public static class DomainServiceCollectionExtensions
{
    public static IServiceCollection AddPaymentDomainPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres.");
        }

        services.TryAddSingleton<SensitiveDataMaterializationInterceptor>();
        services.AddDbContext<PaymentServiceDbContext>((sp, options) =>
        {
            options.UseNpgsql(connectionString, b => b.MigrationsAssembly(typeof(PaymentServiceDbContext).Assembly.FullName));
            var interceptor = sp.GetService<SensitiveDataMaterializationInterceptor>();
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }
            options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IInvoiceRepository, EfInvoiceRepository>();
        services.AddScoped<ISettlementLedger, EfSettlementLedger>();
        services.AddScoped<IManualRefundRepository, ManualRefundRepository>();
        services.AddScoped<IOutboxRepository, EfOutboxRepository>();
        services.AddScoped<ITransactionJournalRepository, EfTransactionJournalRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        return services;
    }

    public static IServiceCollection AddIntegrityEvidencePersistence(
        this IServiceCollection services,
        string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Thiếu ConnectionStrings:IntegrityPostgres.");
        }

        services.AddDbContext<IntegrityDbContext>(options =>
            options.UseNpgsql(connectionString, b => b.MigrationsAssembly(typeof(IntegrityDbContext).Assembly.FullName)));
        services.AddScoped<IIntegrityEvidenceRepository, Dprental.PaymentService.Domain.Repositories.Integrity.EfIntegrityEvidenceRepository>();

        return services;
    }
}
