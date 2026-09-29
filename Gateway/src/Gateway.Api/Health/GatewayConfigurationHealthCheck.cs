using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Ocelot.Configuration;
using Ocelot.Configuration.Repository;

namespace Dprental.Gateway.Health;

/// <summary>
/// Kiểm tra tính sẵn sàng (Readiness) của cấu hình Gateway và Ocelot.
/// Đảm bảo Gateway đã nạp thành công các tuyến đường (routes)
/// trước khi báo cáo sẵn sàng tiếp nhận traffic từ bên ngoài.
/// </summary>
public sealed class GatewayConfigurationHealthCheck : IHealthCheck
{
    private readonly IInternalConfigurationRepository internalConfigurationRepository;

    /// <summary>
    /// Khởi tạo health check với repository cấu hình nội bộ của Ocelot.
    /// </summary>
    /// <param name="internalConfigurationRepository">Bộ cung cấp cấu hình Ocelot hiện tại.</param>
    public GatewayConfigurationHealthCheck(IInternalConfigurationRepository internalConfigurationRepository)
    {
        this.internalConfigurationRepository = internalConfigurationRepository ?? throw new ArgumentNullException(nameof(internalConfigurationRepository));
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            IInternalConfiguration config = internalConfigurationRepository.Get();
            if (config is null || config.Routes is null || config.Routes.Length == 0)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("Cấu hình Ocelot chưa được nạp hoặc không có route nào."));
            }

            return Task.FromResult(HealthCheckResult.Healthy(
                $"Cấu hình Gateway hợp lệ. Đang quản lý {config.Routes.Length} route(s)."));
        }
        catch (Exception exception)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Lỗi khi kiểm tra cấu hình Gateway.", exception));
        }
    }
}
