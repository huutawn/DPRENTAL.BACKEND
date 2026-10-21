using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.RentalService.Domain.DependencyInjection;

/// <summary>
/// Đăng ký PostgreSQL persistence và repository dependencies cho service.
/// </summary>
public static class DomainRepositoriesServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký DbContext và repositories sau khi xác thực cấu hình kết nối.
    /// </summary>
    public static IServiceCollection AddRentalServiceDomainRepositories(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres.");
        }

        services.AddDbContext<RentalServiceDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IRentalCartRepository, RentalCartRepository>();
        services.AddScoped<IRentalOrderRepository, RentalOrderRepository>();
        services.AddScoped<IOrderCodeGenerator, PostgresOrderCodeGenerator>();
        services.AddScoped<IInboxRepository, InboxRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IOperationReceiptRepository, OperationReceiptRepository>();
        services.AddScoped<IRentalCheckoutOperationRepository, RentalCheckoutOperationRepository>();
        services.AddScoped<IRenterAddressRepository, RenterAddressRepository>();
        services.AddSingleton<IOrderLockManager, PostgresOrderLockManager>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<RentalServiceDbContext>());
        return services;
    }
}
