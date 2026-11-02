using Dprental.Gateway.Models.Authentication;
using Dprental.Gateway.Extensions.Authentication;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dprental.Gateway.Extensions.Health;

/// <summary>Chỉ báo sẵn sàng khi Gateway có bộ khóa AuthService còn trong thời hạn sử dụng.</summary>
internal sealed class JwksReadinessHealthCheck(JwksConfigurationManager configurationManager) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await configurationManager.EnsureUsableConfigurationAsync(cancellationToken);
            return configurationManager.HasUsableConfiguration()
                ? HealthCheckResult.Healthy("A valid AuthService JWKS is available.")
                : HealthCheckResult.Unhealthy("The AuthService JWKS cache is stale.");
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("AuthService JWKS is unavailable or invalid.", exception);
        }
    }
}
