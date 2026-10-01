using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Application.Services;
using Dprental.CatalogService.Application.Validators;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.CatalogService.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogServiceApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<CatalogDiscoveryOptions>();
        services.Configure<CatalogDiscoveryOptions>(
            configuration.GetSection(CatalogDiscoveryOptions.SectionName));
        services.AddScoped<EquipmentListQueryValidator>();
        services.AddScoped<ICatalogDiscoveryService, CatalogDiscoveryService>();

        return services;
    }
}