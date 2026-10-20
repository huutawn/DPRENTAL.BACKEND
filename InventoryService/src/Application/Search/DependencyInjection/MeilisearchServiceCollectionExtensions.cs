using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Search;
using Meilisearch;
using Dprental.InventoryService.Application.Search.Adapters;
using Dprental.InventoryService.Application.Search.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Application.Search.DependencyInjection;

/// <summary>
/// Mở rộng IServiceCollection đăng ký hạ tầng Meilisearch tại tầng Application/Search.
/// </summary>
public static class MeilisearchServiceCollectionExtensions
{
    public static IServiceCollection AddInventoryMeilisearch(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(MeilisearchOptions.SectionName);
        services.AddOptions<MeilisearchOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .Validate(options =>
            {
                var context = new System.ComponentModel.DataAnnotations.ValidationContext(options);
                var results = options.Validate(context).ToList();
                return results.Count == 0;
            }, "Cấu hình Meilisearch không hợp lệ hoặc trùng tên index.")
            .ValidateOnStart();

        // HttpClient với timeout 5 giây
        services.AddHttpClient("InventoryMeilisearch", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<MeilisearchOptions>>().Value;
            if (Uri.TryCreate(options.Url, UriKind.Absolute, out var uri))
            {
                client.BaseAddress = uri;
            }
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MeilisearchOptions>>().Value;
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient("InventoryMeilisearch");
            if (httpClient.BaseAddress is null)
            {
                httpClient.BaseAddress = Uri.TryCreate(options.Url, UriKind.Absolute, out var uri)
                    ? uri
                    : new Uri("http://localhost:7700");
            }
            return new MeilisearchClient(httpClient, options.ApiKey);
        });
        services.AddScoped<IInventorySearchIndexService, MeilisearchInventoryIndexService>();
        services.AddScoped<IInventoryIndexProvisioner, MeilisearchIndexProvisioner>();
        services.AddScoped<IInventorySearchBackfillService, MeilisearchBackfillService>();

        services.AddHostedService<SearchProjectionWorker>();
        services.AddHostedService<SearchBootstrapHostedService>();

        return services;
    }
}
