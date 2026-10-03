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
    public static IServiceCollection AddInventoryServiceDomainRepositories(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres.");
        }

        services.AddDbContext<InventoryServiceDbContext>(options => options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName)));

        services.AddScoped<IInventoryAssetRepository, InventoryAssetRepository>();
        services.AddScoped<IAssetAllocationRepository, AssetAllocationRepository>();
        services.AddScoped<ICalendarBlockRepository, CalendarBlockRepository>();

        services.AddScoped<IOutboxStore, OutboxStore>();
        services.AddScoped<IInboxStore, InboxStore>();

        services.AddScoped<IInventoryWriteTransaction, InventoryWriteTransaction>();

        return services;
    }
}
