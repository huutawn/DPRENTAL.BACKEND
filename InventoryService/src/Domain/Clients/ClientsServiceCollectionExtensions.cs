using Dprental.InventoryService.Domain.Clients.Catalog;
using Dprental.InventoryService.Domain.Clients.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Domain.Clients;

/// <summary>
/// Mở rộng IServiceCollection đăng ký các HTTP client (Catalog, Media) thuộc tầng Domain.
/// </summary>
public static class ClientsServiceCollectionExtensions
{
    public static IServiceCollection AddInventoryClients(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MediaServiceOptions>()
            .Bind(configuration.GetSection(MediaServiceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<IMediaServiceClient, MediaServiceClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<MediaServiceOptions>>().Value;
            client.BaseAddress = new Uri(options.InternalBaseUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false
        });

        services.AddOptions<CatalogServiceOptions>()
            .Bind(configuration.GetSection(CatalogServiceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<ICatalogServiceClient, CatalogServiceClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<CatalogServiceOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false
        });

        return services;
    }
}
