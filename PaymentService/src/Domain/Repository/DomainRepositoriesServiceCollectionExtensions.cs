using Dprental.PaymentService.Domain.IRepository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

        services.AddScoped<IInvoiceRepository, EfInvoiceRepository>();

        return services;
    }
}
