using Dprental.AnalyticsService.Application.Hub.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Application.Hub.Security;

/// <summary>
/// Chặn kết nối Hub từ domain ngoài AllowedOrigins (giống PaymentHubOriginGuardMiddleware): trang web lạ
/// không lợi dụng được cookie accessToken của người dùng. Request không có Origin (không phải trình duyệt)
/// được cho qua; xác thực JWT vẫn áp dụng.
/// </summary>
public sealed partial class AnalyticsHubOriginGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AnalyticsHubOptions _options;
    private readonly ILogger<AnalyticsHubOriginGuardMiddleware> _logger;

    public AnalyticsHubOriginGuardMiddleware(
        RequestDelegate next, IOptions<AnalyticsHubOptions> options, ILogger<AnalyticsHubOriginGuardMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_options.AllowedOrigins.Length > 0
            && context.Request.Path.StartsWithSegments(_options.HubPath, StringComparison.OrdinalIgnoreCase)
            && context.Request.Headers.TryGetValue("Origin", out var originValues))
        {
            string origin = originValues.ToString().Trim().TrimEnd('/');
            bool allowed = _options.AllowedOrigins.Any(candidate =>
                string.Equals(candidate.Trim().TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));

            if (!allowed)
            {
                LogRejectedOrigin(_logger, origin);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        await _next(context);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AnalyticsHub connection rejected for origin {Origin}")]
    private static partial void LogRejectedOrigin(ILogger logger, string origin);
}
