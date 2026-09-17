using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dprental.Gateway.Health;

/// <summary>Registers Gateway operational health checks.</summary>
internal static class HealthServiceCollectionExtensions
{
    /// <summary>Registers the AuthService JWKS readiness dependency.</summary>
    internal static IServiceCollection AddGatewayHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<JwksReadinessHealthCheck>(
                "auth-jwks",
                failureStatus: HealthStatus.Unhealthy,
                tags: [GatewayHealthCheckTags.Readiness]);

        return services;
    }
}
