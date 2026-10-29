using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dprental.Gateway.Health;

/// <summary>Đăng ký các phép kiểm tra tình trạng hoạt động của Gateway.</summary>
internal static class HealthServiceCollectionExtensions
{
    /// <summary>Đăng ký readiness cho cả bộ khóa JWKS và cấu hình route Ocelot.</summary>
    internal static IServiceCollection AddGatewayHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<JwksReadinessHealthCheck>(
                "auth-jwks",
                failureStatus: HealthStatus.Unhealthy,
                tags: [GatewayHealthCheckTags.Readiness])
            .AddCheck<GatewayConfigurationHealthCheck>(
                "proxy-config",
                failureStatus: HealthStatus.Unhealthy,
                tags: [GatewayHealthCheckTags.Readiness]);

        return services;
    }
}
