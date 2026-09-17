using Dprental.Gateway.Infrastructure.Traffic.Resilience;
using Microsoft.AspNetCore.Builder;

namespace Dprental.Gateway.Traffic;

/// <summary>
/// Cung cấp các phương thức mở rộng tích hợp các middleware điều phối lưu lượng và phục hồi vào HTTP Pipeline.
/// </summary>
public static class TrafficApplicationBuilderExtensions
{
    /// <summary>
    /// Kích hoạt chuỗi middleware Traffic Control và Resilience:
    /// 1. UpstreamTimeoutMiddleware: Ngăn chặn treo kết nối, bảo đảm timeout budget và trả về 504 Gateway Timeout.
    /// 2. UseRateLimiter: Kiểm soát tần suất request theo phân tầng (Anonymous, Authenticated, Sensitive).
    /// </summary>
    /// <param name="app">Đối tượng IApplicationBuilder xây dựng HTTP pipeline.</param>
    /// <returns>Đối tượng IApplicationBuilder để xâu chuỗi.</returns>
    public static IApplicationBuilder UseGatewayTrafficControl(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // 1. Quản lý ngân sách thời gian chờ upstream
        app.UseMiddleware<UpstreamTimeoutMiddleware>();

        // 2. Kiểm soát tần suất truy cập Rate Limiting
        app.UseRateLimiter();

        return app;
    }
}
