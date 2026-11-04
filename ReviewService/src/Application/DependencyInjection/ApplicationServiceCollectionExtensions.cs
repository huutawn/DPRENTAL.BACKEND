using Dprental.ReviewService.Application.Clients;
using Dprental.ReviewService.Application.Options;
using Dprental.ReviewService.Application.Services;
using Dprental.ReviewService.Application.Kafka.Producers;
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

            var mediaSection = configuration.GetSection(MediaServiceOptions.SectionName);
            services.Configure<MediaServiceOptions>(mediaSection);
        }

        services.AddScoped<IIdempotencyService, IdempotencyService>();
        services.AddScoped<IReviewAuthoringService, ReviewAuthoringService>();
        services.AddScoped<IReviewQueryService, ReviewQueryService>();
        services.AddScoped<IReviewModerationService, ReviewModerationService>();
        services.AddScoped<IReviewMediaService, ReviewMediaService>();
        services.AddScoped<IReviewEligibilityService, ReviewEligibilityService>();
        services.AddScoped<Dprental.ReviewService.Application.Kafka.Consumers.IRentalItemCompletedConsumer, Dprental.ReviewService.Application.Kafka.Consumers.RentalItemCompletedConsumer>();
        services.AddSingleton<IKafkaProducer, KafkaProducer>();
        services.AddHostedService<Dprental.ReviewService.Application.Kafka.Workers.OutboxPublisherWorker>();
        services.AddHostedService<Dprental.ReviewService.Application.Kafka.Workers.RentalReconciliationSweepWorker>();
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

        services.AddHttpClient<IMediaServiceClient, MediaServiceClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetService<IOptions<MediaServiceOptions>>()?.Value ?? new MediaServiceOptions();

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
