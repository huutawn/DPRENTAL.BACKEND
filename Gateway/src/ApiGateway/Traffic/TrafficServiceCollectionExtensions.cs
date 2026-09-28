using Dprental.Gateway.Application.Traffic;
using Dprental.Gateway.Infrastructure.Traffic.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.Gateway.Traffic;

/// <summary>
/// Cung cấp các phương thức mở rộng đăng ký giới hạn tần suất vào DI container.
/// </summary>
public static class TrafficServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký cấu hình và chính sách Rate Limiting.
    /// </summary>
    /// <param name="services">Tập hợp dịch vụ DI container.</param>
    /// <param name="configuration">Cấu hình hệ thống IConfiguration.</param>
    /// <returns>Tập hợp dịch vụ IServiceCollection để xâu chuỗi.</returns>
    public static IServiceCollection AddGatewayTrafficControl(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 1. Cấu hình và thẩm định GatewayTrafficOptions
        services.AddSingleton<IValidateOptions<GatewayTrafficOptions>, GatewayTrafficOptionsValidator>();
        services.AddOptions<GatewayTrafficOptions>()
            .Bind(configuration.GetSection(GatewayTrafficOptions.SectionName))
            .ValidateOnStart();

        // Đăng ký ASP.NET Core RateLimiter với cấu hình phân tầng
        services.AddRateLimiter();
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<GatewayTrafficOptions>, ILoggerFactory>((options, trafficOptions, loggerFactory) =>
            {
                ILogger logger = loggerFactory.CreateLogger("Dprental.Gateway.Traffic.RateLimiter");
                GatewayRateLimitingConfiguration.Configure(options, trafficOptions.Value, logger);
            });

        return services;
    }
}
