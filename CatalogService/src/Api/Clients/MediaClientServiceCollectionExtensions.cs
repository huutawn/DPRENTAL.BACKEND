using Dprental.CatalogService.Application.Clients;
using Microsoft.Extensions.Options;

namespace Dprental.CatalogService.Api.Clients;

/// <summary>Đăng ký HTTP client gọi internal API của Media Service.</summary>
internal static class MediaClientServiceCollectionExtensions
{
    internal static IServiceCollection AddMediaVerificationClient(this IServiceCollection services, IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection(MediaServiceOptions.SectionName);
        MediaServiceOptions options = section.Get<MediaServiceOptions>() ?? new MediaServiceOptions();
        ValidateOptionsResult validation = new MediaServiceOptionsValidator().Validate(null, options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(MediaServiceOptions.SectionName, typeof(MediaServiceOptions), validation.Failures);
        }

        services.AddSingleton<IValidateOptions<MediaServiceOptions>, MediaServiceOptionsValidator>();
        services.AddOptions<MediaServiceOptions>()
            .Bind(section)
            .ValidateOnStart();

        services.AddHttpClient<IMediaVerificationClient, MediaVerificationClient>(httpClient =>
        {
            // Dấu "/" cuối giữ nguyên path prefix của BaseUrl (ví dụ khi đi qua Gateway) khi ghép đường dẫn tương đối.
            httpClient.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            httpClient.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            httpClient.DefaultRequestHeaders.Add(MediaVerificationClient.ApiKeyHeaderName, options.ApiKey);
        });

        return services;
    }
}
