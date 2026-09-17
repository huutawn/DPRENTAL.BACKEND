using Dprental.Gateway.Application.Security;
using Microsoft.AspNetCore.Http;

namespace Dprental.Gateway.Infrastructure.Security.Middlewares;

/// <summary>
/// Middleware làm sạch tiêu đề HTTP (Header Sanitization), chạy ở giai đoạn đầu của pipeline
/// nhằm loại bỏ ngay các header nhạy cảm do client tự ý gửi lên để chống tấn công giả mạo danh tính (Identity Spoofing).
/// </summary>
public sealed class HeaderSanitizationMiddleware
{
    private readonly RequestDelegate next;
    private readonly IHeaderSanitizer headerSanitizer;

    /// <summary>
    /// Khởi tạo middleware làm sạch header.
    /// </summary>
    /// <param name="next">Delegate chuyển tiếp tới middleware tiếp theo trong pipeline.</param>
    /// <param name="headerSanitizer">Quy tắc nghiệp vụ làm sạch tiêu đề.</param>
    public HeaderSanitizationMiddleware(RequestDelegate next, IHeaderSanitizer headerSanitizer)
    {
        this.next = next ?? throw new ArgumentNullException(nameof(next));
        this.headerSanitizer = headerSanitizer ?? throw new ArgumentNullException(nameof(headerSanitizer));
    }

    /// <summary>
    /// Thực thi kiểm tra và làm sạch toàn bộ tiêu đề không an toàn từ client.
    /// </summary>
    /// <param name="context">Ngữ cảnh HTTP của request hiện tại.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Loại bỏ các header untrusted khỏi context.Request.Headers
        headerSanitizer.SanitizeInboundHeaders(context.Request.Headers);

        await next(context);
    }
}
