using Dprental.Gateway.Application.Security;
using Dprental.Gateway.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Dprental.Gateway.Infrastructure.Security.Middlewares;

/// <summary>
/// Middleware kiểm soát và thực thi chính sách CORS (Cross-Origin Resource Sharing).
/// Thẩm định nguồn gốc (Origin) theo danh sách whitelist, xử lý request tiền trạm (preflight OPTIONS)
/// và chủ động từ chối (403 Forbidden) đối với các origin không hợp lệ.
/// </summary>
public sealed class GatewayCorsMiddleware
{
    private static readonly Action<ILogger, string, Exception?> LogCorsOriginDenied = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(1, "CorsOriginDenied"),
        "Yêu cầu từ Origin '{RequestOrigin}' bị từ chối do không nằm trong danh sách whitelist của chính sách CORS.");

    private readonly RequestDelegate next;
    private readonly IOptions<GatewaySecurityOptions> securityOptions;
    private readonly ILogger<GatewayCorsMiddleware> logger;

    /// <summary>
    /// Khởi tạo middleware CORS với cấu hình bảo mật và logger.
    /// </summary>
    /// <param name="next">Delegate chuyển tiếp middleware tiếp theo.</param>
    /// <param name="securityOptions">Cấu hình bảo mật Gateway.</param>
    /// <param name="logger">Logger ghi nhận cảnh báo vi phạm CORS.</param>
    public GatewayCorsMiddleware(
        RequestDelegate next,
        IOptions<GatewaySecurityOptions> securityOptions,
        ILogger<GatewayCorsMiddleware> logger)
    {
        this.next = next ?? throw new ArgumentNullException(nameof(next));
        this.securityOptions = securityOptions ?? throw new ArgumentNullException(nameof(securityOptions));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Kiểm tra tính hợp lệ của Origin trong request và áp dụng các header CORS tương ứng.
    /// </summary>
    /// <param name="context">Ngữ cảnh HTTP của request.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 1. Kiểm tra xem request có chứa header Origin hay không
        if (!context.Request.Headers.TryGetValue(HeaderNames.Origin, out var originHeader) ||
            string.IsNullOrWhiteSpace(originHeader.FirstOrDefault()))
        {
            // Không phải cross-origin request (request nội bộ, same-origin hoặc từ backend client)
            await next(context);
            return;
        }

        string requestOrigin = originHeader.ToString().Trim();
        GatewayCorsOptions corsOptions = securityOptions.Value.Cors;

        // 2. Kiểm tra Origin có nằm trong danh sách whitelist cho phép hay không
        if (!IsOriginAllowed(requestOrigin, corsOptions))
        {
            LogCorsOriginDenied(logger, requestOrigin, null);

            // Từ chối request theo tiêu chuẩn bảo mật với mã lỗi chuẩn của Gateway
            await GatewayErrorResponseWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                GatewayError.CorsOriginDenied,
                context.RequestAborted);
            return;
        }

        // 3. Xử lý preflight request (OPTIONS với header Access-Control-Request-Method)
        if (HttpMethods.IsOptions(context.Request.Method) &&
            context.Request.Headers.ContainsKey(HeaderNames.AccessControlRequestMethod))
        {
            ApplyPreflightHeaders(context.Response, requestOrigin, corsOptions);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // 4. Áp dụng các header CORS cho request thực tế
        ApplyActualRequestHeaders(context.Response, requestOrigin, corsOptions);

        await next(context);
    }

    private static bool IsOriginAllowed(string origin, GatewayCorsOptions corsOptions)
    {
        if (corsOptions.AllowedOrigins.Length == 0)
        {
            return false;
        }

        string normalizedOrigin = origin.TrimEnd('/');

        foreach (string allowed in corsOptions.AllowedOrigins)
        {
            if (string.Equals(allowed, "*", StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(allowed.TrimEnd('/'), normalizedOrigin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyPreflightHeaders(HttpResponse response, string origin, GatewayCorsOptions corsOptions)
    {
        response.Headers[HeaderNames.AccessControlAllowOrigin] = origin;
        response.Headers[HeaderNames.AccessControlAllowMethods] = string.Join(", ", corsOptions.AllowedMethods);
        response.Headers[HeaderNames.AccessControlAllowHeaders] = string.Join(", ", corsOptions.AllowedHeaders);

        if (corsOptions.AllowCredentials && !string.Equals(origin, "*", StringComparison.Ordinal))
        {
            response.Headers[HeaderNames.AccessControlAllowCredentials] = "true";
        }

        if (corsOptions.PreflightMaxAgeSeconds > 0)
        {
            response.Headers[HeaderNames.AccessControlMaxAge] = corsOptions.PreflightMaxAgeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        response.Headers[HeaderNames.Vary] = HeaderNames.Origin;
    }

    private static void ApplyActualRequestHeaders(HttpResponse response, string origin, GatewayCorsOptions corsOptions)
    {
        response.Headers[HeaderNames.AccessControlAllowOrigin] = origin;

        if (corsOptions.AllowCredentials && !string.Equals(origin, "*", StringComparison.Ordinal))
        {
            response.Headers[HeaderNames.AccessControlAllowCredentials] = "true";
        }

        if (corsOptions.ExposedHeaders.Length > 0)
        {
            response.Headers[HeaderNames.AccessControlExposeHeaders] = string.Join(", ", corsOptions.ExposedHeaders);
        }

        response.Headers[HeaderNames.Vary] = HeaderNames.Origin;
    }
}
