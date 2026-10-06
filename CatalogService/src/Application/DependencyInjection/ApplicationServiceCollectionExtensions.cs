using CatalogService.Application.IServices;
using CatalogService.Application.Services;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Application.Services;
using Dprental.CatalogService.Application.Validators;
using Dprental.CatalogService.Domain.IRepositories;
using Dprental.CatalogService.Domain.Repositories;
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
        services.AddScoped<EquipmentTypeListQueryValidator>();
        services.AddScoped<CreateEquipmentTypeRequestValidator>();
        services.AddScoped<UpdateEquipmentTypeRequestValidator>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<ISchemaService, SchemaService>();
        services.AddScoped<IEquipmentTypeSchemaRepository, EquipmentTypeSchemaRepository>();
        services.AddScoped<ISchemaQueryService, SchemaQueryService>();
        services.AddScoped<CanonicalEquipmentTypeRules>();
        services.AddScoped<IEquipmentTypeService, EquipmentTypeService>();


        return services;
    }
}