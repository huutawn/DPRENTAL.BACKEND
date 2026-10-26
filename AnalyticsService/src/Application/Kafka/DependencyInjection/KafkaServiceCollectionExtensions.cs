using Dprental.AnalyticsService.Application.Kafka.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Application.Kafka.DependencyInjection;

/// <summary>
/// Đăng ký hạ tầng Kafka cho AnalyticsService.
/// </summary>
public static class KafkaServiceCollectionExtensions
{
    /// <summary>
    /// Bind section <c>Kafka</c> và kiểm tra cấu hình ngay khi ứng dụng khởi động.
    /// </summary>
    public static IServiceCollection AddAnalyticsKafka(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<KafkaOptions>()
            .Bind(configuration.GetSection(KafkaOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<KafkaOptions>, KafkaOptionsValidator>();

        return services;
    }
}
