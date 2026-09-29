using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dprental.Gateway.Observability;

/// <summary>
/// Cung cấp các phương thức mở rộng đăng ký dịch vụ giám sát vận hành (Observability & Logging)
/// vào DI container và HTTP Pipeline.
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký cấu hình Observability (Logging & Metrics) vào hệ thống.
    /// </summary>
    /// <param name="services">Tập hợp dịch vụ DI container.</param>
    /// <param name="configuration">Cấu hình hệ thống IConfiguration.</param>
    /// <returns>Tập hợp dịch vụ IServiceCollection để xâu chuỗi.</returns>
    public static IServiceCollection AddGatewayObservability(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IValidateOptions<GatewayObservabilityOptions>, GatewayObservabilityOptionsValidator>();
        services.AddOptions<GatewayObservabilityOptions>()
            .Bind(configuration.GetSection(GatewayObservabilityOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<GatewayMetrics>();

        return services;
    }

    /// <summary>
    /// Kích hoạt middleware ghi log có cấu trúc (Structured Logging) cho Gateway.
    /// </summary>
    /// <param name="app">Đối tượng IApplicationBuilder xây dựng HTTP pipeline.</param>
    /// <returns>Đối tượng IApplicationBuilder để xâu chuỗi.</returns>
    public static IApplicationBuilder UseGatewayRequestLogging(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<GatewayRequestLoggingMiddleware>();

        return app;
    }

    /// <summary>
    /// Kích hoạt middleware thu thập chỉ số đo lường (Metrics Instrumentation) cho Gateway.
    /// </summary>
    /// <param name="app">Đối tượng IApplicationBuilder xây dựng HTTP pipeline.</param>
    /// <returns>Đối tượng IApplicationBuilder để xâu chuỗi.</returns>
    public static IApplicationBuilder UseGatewayMetrics(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<GatewayMetricsMiddleware>();

        return app;
    }
}
