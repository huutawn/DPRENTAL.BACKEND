using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Mở rộng IServiceCollection đăng ký toàn bộ hạ tầng persistence và repository contracts cho InventoryService.
/// </summary>
public static class DomainRepositoriesServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký DbContext và các repository implementations sau khi kiểm tra tính hợp lệ của chuỗi kết nối PostgreSQL.
    /// </summary>
    /// <param name="services">Tập hợp các service trong DI container.</param>
    /// <param name="connectionString">Chuỗi kết nối tới cơ sở dữ liệu PostgreSQL.</param>
    /// <returns>IServiceCollection phục vụ gọi nối tiếp.</returns>
    /// <exception cref="InvalidOperationException">Ném ra nếu chuỗi kết nối rỗng hoặc không hợp lệ.</exception>
    public static IServiceCollection AddInventoryServiceDomainRepositories(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres.");
        }

        // Đăng ký DbContext với Npgsql provider
        services.AddDbContext<InventoryServiceDbContext>(options => options.UseNpgsql(connectionString));

        // Đăng ký các repository cho các aggregate nghiệp vụ
        services.AddScoped<IInventoryEquipmentRepository, InventoryEquipmentRepository>();
        services.AddScoped<IReservationRepository, ReservationRepository>();
        services.AddScoped<ICalendarBlockRepository, CalendarBlockRepository>();
        services.AddScoped<IDamageReportRepository, DamageReportRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();

        // Đăng ký các store phục vụ Messaging Infrastructure
        services.AddScoped<IOutboxStore, OutboxStore>();
        services.AddScoped<IInboxStore, InboxStore>();
        services.AddScoped<ISagaStore, SagaStore>();

        // Đăng ký bộ điều phối giao dịch ghi độc quyền (Inventory Write Transaction Executor)
        services.AddScoped<IInventoryWriteTransaction, InventoryWriteTransaction>();

        return services;
    }
}
