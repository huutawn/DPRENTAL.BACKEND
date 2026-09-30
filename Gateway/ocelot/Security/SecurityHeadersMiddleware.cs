using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dprental.Gateway.Security;

/// <summary>
/// Middleware áp dụng các tiêu đề bảo mật HTTP (Security Headers) tiêu chuẩn lên mọi phản hồi từ Gateway,
/// giúp bảo vệ ứng dụng trước các lỗ hổng web phổ biến như Clickjacking, MIME-type sniffing và XSS.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate next;
    private readonly IOptions<GatewaySecurityOptions> securityOptions;

    /// <summary>
    /// Khởi tạo middleware gắn security headers.
    /// </summary>
    /// <param name="next">Delegate chuyển tiếp middleware tiếp theo.</param>
    /// <param name="securityOptions">Cấu hình các tiêu đề bảo mật.</param>
    public SecurityHeadersMiddleware(RequestDelegate next, IOptions<GatewaySecurityOptions> securityOptions)
    {
        this.next = next ?? throw new ArgumentNullException(nameof(next));
        this.securityOptions = securityOptions ?? throw new ArgumentNullException(nameof(securityOptions));
    }

    /// <summary>
    /// Gắn sự kiện OnStarting để áp dụng các header bảo mật trước khi phản hồi được gửi về cho client.
    /// </summary>
    /// <param name="context">Ngữ cảnh HTTP của request.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        GatewaySecurityHeadersOptions options = securityOptions.Value.Headers;

        context.Response.OnStarting(() =>
        {
            ApplySecurityHeaders(context.Response, options, context.Request.IsHttps);
            return Task.CompletedTask;
        });

        await next(context);
    }

    private static void ApplySecurityHeaders(HttpResponse response, GatewaySecurityHeadersOptions options, bool isHttps)
    {
        IHeaderDictionary headers = response.Headers;

        // 1. Chống MIME-sniffing
        if (options.EnableContentTypeOptions && !headers.ContainsKey("X-Content-Type-Options"))
        {
            headers["X-Content-Type-Options"] = "nosniff";
        }

        // 2. Chống Clickjacking (Frame-Options)
        if (options.EnableFrameOptions && !headers.ContainsKey("X-Frame-Options"))
        {
            headers["X-Frame-Options"] = options.FrameOptions;
        }

        // 3. Vô hiệu hóa XSS filter cũ có lỗ hổng
        if (options.EnableXssProtection && !headers.ContainsKey("X-XSS-Protection"))
        {
            headers["X-XSS-Protection"] = options.XssProtection;
        }

        // 4. Kiểm soát chính sách Referrer
        if (options.EnableReferrerPolicy && !headers.ContainsKey("Referrer-Policy"))
        {
            headers["Referrer-Policy"] = options.ReferrerPolicy;
        }

        // 5. Chính sách bảo mật nội dung CSP
        if (options.EnableContentSecurityPolicy && !headers.ContainsKey("Content-Security-Policy"))
        {
            headers["Content-Security-Policy"] = options.ContentSecurityPolicy;
        }

        // 6. Bắt buộc HTTPS qua HSTS khi request là HTTPS
        if (isHttps && options.EnableStrictTransportSecurity && !headers.ContainsKey("Strict-Transport-Security"))
        {
            string hstsValue = options.HstsIncludeSubDomains
                ? $"max-age={options.HstsMaxAgeSeconds}; includeSubDomains"
                : $"max-age={options.HstsMaxAgeSeconds}";

            headers["Strict-Transport-Security"] = hstsValue;
        }
    }
}
