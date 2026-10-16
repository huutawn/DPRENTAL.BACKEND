using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Application.Locations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace Dprental.InventoryService.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký các application services của Inventory Service với Scoped lifetime theo chuẩn Clean Architecture.
    /// </summary>
    public static IServiceCollection AddInventoryApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IVietnamDivisionsCatalog, VietnamDivisionsCatalog>();
        services.AddSingleton<AssetSearchQueryValidator>();
        services.AddScoped<AssetSearchFilterResolver>();
        services.AddScoped<IEquipmentListingService, EquipmentListingService>();
        services.AddScoped<IWarehouseService, WarehouseService>();
        services.AddScoped<IInventoryAssetService, InventoryAssetService>();
        services.AddScoped<IPublicEquipmentListingService, PublicEquipmentListingService>();
        services.AddScoped<ICalendarService, CalendarService>();
        services.AddScoped<IInventoryMediaService, InventoryMediaService>();
        services.AddScoped<IInventoryAllocationService, InventoryAllocationService>();
        services.AddOptions<InventoryCacheOptions>();
        services.TryAddSingleton<IInventoryAvailabilityCache>(NoOpInventoryAvailabilityCache.Instance);
        services.AddScoped<IInventoryAvailabilityService, InventoryAvailabilityService>();

        return services;
    }
}
