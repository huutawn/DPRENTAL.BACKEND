using Dprental.Gateway.Infrastructure.Security.Middlewares;
using Microsoft.AspNetCore.Builder;

namespace Dprental.Gateway.Security;

/// <summary>
/// Cung cấp các phương thức mở rộng để tích hợp các middleware bảo mật vào HTTP Request Pipeline của Gateway.
/// </summary>
public static class SecurityApplicationBuilderExtensions
{
    /// <summary>
    /// Kích hoạt chuỗi middleware bảo vệ và kiểm soát an ninh request cho Gateway.
    /// Thứ tự thực thi:
    /// 1. ForwardedHeaders: Chuẩn hóa IP/Scheme từ reverse proxy hoặc load balancer phía trước.
    /// 2. SecurityHeaders: Gắn các tiêu đề bảo mật HTTP (X-Content-Type-Options, CSP, HSTS, v.v.).
    /// 3. HeaderSanitization: Loại bỏ ngay các tiêu đề nhạy cảm client tự ý gửi lên.
    /// 4. RequestSizeProtection: Chặn các request có dung lượng vượt quá giới hạn an toàn.
    /// 5. GatewayCors: Thẩm định nguồn gốc (Origin) theo whitelist và từ chối các request không hợp lệ.
    /// </summary>
    /// <param name="app">Đối tượng IApplicationBuilder xây dựng HTTP pipeline.</param>
    /// <returns>Đối tượng IApplicationBuilder để xâu chuỗi.</returns>
    public static IApplicationBuilder UseGatewaySecurity(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // 1. Forwarded Headers
        app.UseForwardedHeaders();

        // 2. Security Headers
        app.UseMiddleware<SecurityHeadersMiddleware>();

        // 3. Header Sanitization
        app.UseMiddleware<HeaderSanitizationMiddleware>();

        // 4. Request Size Protection
        app.UseMiddleware<RequestSizeProtectionMiddleware>();

        // 5. Gateway CORS Policy
        app.UseMiddleware<GatewayCorsMiddleware>();

        return app;
    }
}
