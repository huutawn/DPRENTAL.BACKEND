using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Infrastructure.Services;

namespace Dprental.PaymentService.Infrastructure.Persistence;

/// <summary>
/// Đăng ký PostgreSQL persistence và Infrastructure services.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký DbContext sau khi xác thực cấu hình kết nối.
    /// </summary>
    public static IServiceCollection AddPaymentServiceInfrastructure(
        this IServiceCollection services,
        string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres.");
        }
        services.AddScoped<IInvoiceService, InvoiceService>();

        services.AddDbContext<PaymentServiceDbContext>(options =>
            options.UseNpgsql(connectionString));

        return services;
    }
}
