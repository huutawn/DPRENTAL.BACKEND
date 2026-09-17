using Dprental.Gateway.Application.Traffic;
using Dprental.Gateway.Infrastructure.Traffic.RateLimiting;
using Dprental.Gateway.Infrastructure.Traffic.Resilience;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.Gateway.Traffic;

/// <summary>
/// Cung cấp các phương thức mở rộng đăng ký dịch vụ điều phối lưu lượng (Traffic Control)
/// và khả năng phục hồi (Resilience) vào DI container.
/// </summary>
public static class TrafficServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký toàn bộ cấu hình, chính sách Rate Limiting, Timeout và an toàn thử lại (Retry Safety).
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

        // 2. Đăng ký chính sách cấm thử lại lệnh nghiệp vụ không an toàn (No Unsafe Retry Policy)
        services.AddSingleton<NoUnsafeRetryPolicy>();
        // 3. Đăng ký ASP.NET Core RateLimiter với cấu hình phân tầng
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
