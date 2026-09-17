using Microsoft.Extensions.Diagnostics.HealthChecks;
using Yarp.ReverseProxy.Configuration;

namespace Dprental.Gateway.Health;

/// <summary>
/// Kiểm tra tính sẵn sàng (Readiness) của cấu hình Gateway và Reverse Proxy YARP.
/// Đảm bảo Gateway đã nạp thành công các tuyến đường (routes) và cụm dịch vụ (clusters)
/// trước khi báo cáo sẵn sàng tiếp nhận traffic từ bên ngoài.
/// </summary>
public sealed class GatewayConfigurationHealthCheck : IHealthCheck
{
    private readonly IProxyConfigProvider proxyConfigProvider;

    /// <summary>
    /// Khởi tạo health check với provider cấu hình của YARP.
    /// </summary>
    /// <param name="proxyConfigProvider">Bộ cung cấp cấu hình proxy hiện tại.</param>
    public GatewayConfigurationHealthCheck(IProxyConfigProvider proxyConfigProvider)
    {
        this.proxyConfigProvider = proxyConfigProvider ?? throw new ArgumentNullException(nameof(proxyConfigProvider));
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            IProxyConfig config = proxyConfigProvider.GetConfig();
            if (config is null || config.Routes is null || config.Clusters is null)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("Cấu hình YARP Reverse Proxy chưa được nạp."));
            }

            return Task.FromResult(HealthCheckResult.Healthy(
                $"Cấu hình Gateway hợp lệ. Đang quản lý {config.Routes.Count} route(s) và {config.Clusters.Count} cluster(s)."));
        }
        catch (Exception exception)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Lỗi khi kiểm tra cấu hình Gateway.", exception));
        }
    }
}
