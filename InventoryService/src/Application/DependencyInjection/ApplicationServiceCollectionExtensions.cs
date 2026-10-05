using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.InventoryService.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký các application services của Inventory Service với Scoped lifetime theo chuẩn Clean Architecture.
    /// </summary>
    public static IServiceCollection AddInventoryApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IInventoryAssetService, InventoryAssetService>();
        services.AddScoped<ICalendarService, CalendarService>();
        services.AddScoped<IInventoryMediaService, InventoryMediaService>();
        services.AddScoped<IInventoryAllocationService, InventoryAllocationService>();

        return services;
    }
}
