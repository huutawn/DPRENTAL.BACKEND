using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.NotificationService.Domain.Repositories;

/// <summary>
/// Đăng ký PostgreSQL persistence và repositories cho service.
/// </summary>
public static class DomainRepositoriesServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký DbContext và Repositories sau khi xác thực cấu hình kết nối.
    /// </summary>
    public static IServiceCollection AddNotificationServiceDomainRepositories(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres.");
        }

        services.AddDbContext<NotificationServiceDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IInboxReceiptRepository, InboxReceiptRepository>();
        services.AddScoped<IDeliveryJobRepository, DeliveryJobRepository>();
        services.AddScoped<IPreferenceRepository, PreferenceRepository>();
        services.AddScoped<IAggregateWatermarkRepository, AggregateWatermarkRepository>();

        return services;
    }
}
