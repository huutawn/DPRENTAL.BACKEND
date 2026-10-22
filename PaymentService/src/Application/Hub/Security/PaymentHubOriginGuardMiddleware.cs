#pragma warning disable CA1848
using Dprental.PaymentService.Application.Hub.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Hub.Security;

public sealed class PaymentHubOriginGuardMiddleware(
    RequestDelegate next,
    IOptions<PaymentHubOptions> options,
    ILogger<PaymentHubOriginGuardMiddleware> logger)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly PaymentHubOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<PaymentHubOriginGuardMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(_options.HubPath, StringComparison.OrdinalIgnoreCase))
        {
            if (context.Request.Headers.TryGetValue("Origin", out var originValues))
            {
                string origin = originValues.ToString().Trim().TrimEnd('/');
                bool isAllowed = _options.AllowedOrigins.Any(allowed =>
                    string.Equals(allowed.Trim().TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));

                if (!isAllowed)
                {
                    _logger.LogWarning("PaymentHub connection rejected due to unallowed Origin: {Origin}", origin);
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
        }

        await _next(context);
    }
}
