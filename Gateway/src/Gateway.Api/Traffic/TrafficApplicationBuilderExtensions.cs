using Microsoft.AspNetCore.Builder;

namespace Dprental.Gateway.Traffic;

/// <summary>
/// Cung cấp các phương thức mở rộng tích hợp các middleware điều phối lưu lượng và phục hồi vào HTTP Pipeline.
/// </summary>
public static class TrafficApplicationBuilderExtensions
{
    /// <summary>
    /// Kích hoạt chuỗi middleware Traffic Control:
    /// Kiểm soát tần suất request theo phân tầng (Anonymous, Authenticated, Sensitive) qua UseRateLimiter.
    /// </summary>
    /// <param name="app">Đối tượng IApplicationBuilder xây dựng HTTP pipeline.</param>
    /// <returns>Đối tượng IApplicationBuilder để xâu chuỗi.</returns>
    public static IApplicationBuilder UseGatewayTrafficControl(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Kiểm soát tần suất truy cập Rate Limiting
        app.UseRateLimiter();

        return app;
    }
}
