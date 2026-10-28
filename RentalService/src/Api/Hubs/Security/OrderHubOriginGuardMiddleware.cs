using Dprental.RentalService.Application.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Api.Hubs.Security;

/// <summary>
/// Kiểm soát Origin cho browser kết nối OrderHub.
/// Browser có Origin không nằm trong allowlist sẽ bị chặn 403 Forbidden.
/// Non-browser không có header Origin vẫn tiếp tục và phải vượt qua chính sách xác thực JWT.
/// </summary>
public sealed partial class OrderHubOriginGuardMiddleware(
    RequestDelegate next,
    IOptions<OrderHubOptions> options,
    ILogger<OrderHubOriginGuardMiddleware> logger)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly OrderHubOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<OrderHubOriginGuardMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task InvokeAsync(HttpContext context)
    {
        if (OrderHubPathHelper.IsOrderHubPath(context.Request.Path))
        {
            if (context.Request.Headers.TryGetValue("Origin", out var originValues))
            {
                string origin = originValues.ToString().Trim().TrimEnd('/');
                bool isAllowed = !string.IsNullOrWhiteSpace(origin) &&
                    _options.AllowedOrigins.Any(allowed =>
                        string.Equals(allowed.Trim().TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));

                if (!isAllowed)
                {
                    LogUnallowedOrigin(_logger, origin);
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
        }

        await _next(context);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "OrderHub connection rejected due to unallowed Origin: '{Origin}'.")]
    private static partial void LogUnallowedOrigin(ILogger logger, string origin);
}
