using Dprental.InventoryService.Api.Infrastructure.Repositories;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.InventoryService.Api.Infrastructure.DependencyInjection;

/// <summary>
/// Mở rộng IServiceCollection đăng ký các repository implementation thuộc tầng Infrastructure của Inventory API.
/// </summary>
public static class InventoryInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInventoryApiRepositories(this IServiceCollection services)
    {
        services.AddScoped<IInventoryAssetRepository, InventoryAssetRepository>();
        return services;
    }
}
