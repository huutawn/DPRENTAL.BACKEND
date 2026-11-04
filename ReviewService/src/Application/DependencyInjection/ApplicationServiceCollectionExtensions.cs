using Dprental.ReviewService.Application.Clients;
using Dprental.ReviewService.Application.Options;
using Dprental.ReviewService.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dprental.ReviewService.Application.DependencyInjection;

/// <summary>
/// Các phương thức mở rộng đăng ký dịch vụ Application vào Dependency Injection container.
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký toàn bộ các dịch vụ Application (IdempotencyService, RentalServiceClient, Options).
    /// </summary>
    /// <param name="services">Tập hợp dịch vụ IServiceCollection.</param>
    /// <param name="configuration">Cấu hình IConfiguration từ ứng dụng (tùy chọn).</param>
    /// <returns>Tập hợp dịch vụ sau khi đăng ký.</returns>
    public static IServiceCollection AddReviewServiceApplication(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configuration != null)
        {
            var rentalSection = configuration.GetSection(RentalServiceOptions.SectionName);
            services.Configure<RentalServiceOptions>(rentalSection);
        }

        services.AddScoped<IIdempotencyService, IdempotencyService>();
        services.AddScoped<IReviewService, Dprental.ReviewService.Application.Services.ReviewService>();
        services.AddSingleton(TimeProvider.System);

        services.AddHttpClient<IRentalServiceClient, RentalServiceClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetService<IOptions<RentalServiceOptions>>()?.Value ?? new RentalServiceOptions();

            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                client.BaseAddress = new Uri(options.BaseUrl);
            }

            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", options.ApiKey);
            }

            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 5);
        });

        return services;
    }
}
