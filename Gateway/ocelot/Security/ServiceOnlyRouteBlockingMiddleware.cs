using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.Gateway.Security;

/// <summary>
/// Chặn các endpoint chỉ dành cho service (Service-only) và yêu cầu JWT người dùng đối với các endpoint dùng chung (dual-purpose) khi đi qua Gateway.
/// </summary>
public sealed class ServiceOnlyRouteBlockingMiddleware
{
    private readonly RequestDelegate _next;

    public ServiceOnlyRouteBlockingMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string method = context.Request.Method;
        string path = context.Request.Path.Value ?? string.Empty;
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length >= 2 &&
            segments[0].Equals("hubs", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("payments", StringComparison.OrdinalIgnoreCase))
        {
            if (context.User?.Identity?.IsAuthenticated != true)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                var problem = new ProblemDetails
                {
                    Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                    Title = "Unauthorized",
                    Status = StatusCodes.Status401Unauthorized,
                    Detail = "User authentication is required to access Payment Hub.",
                    Instance = context.Request.Path
                };
                await context.Response.WriteAsJsonAsync(problem);
                return;
            }

            if (context.User.HasClaim("u", "svc") || string.Equals(context.User.FindFirst("u")?.Value, "svc", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/problem+json";
                var problem = new ProblemDetails
                {
                    Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                    Title = "Forbidden",
                    Status = StatusCodes.Status403Forbidden,
                    Detail = "Service tokens are not permitted to connect to Payment Hub.",
                    Instance = context.Request.Path
                };
                await context.Response.WriteAsJsonAsync(problem);
                return;
            }
        }

        if (IsServiceOnlyRoute(method, segments))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            var problem = new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                Title = "Forbidden",
                Status = StatusCodes.Status403Forbidden,
                Detail = "API endpoint is internal service-only and cannot be accessed via Gateway.",
                Instance = context.Request.Path
            };
            await context.Response.WriteAsJsonAsync(problem);
            return;
        }

        if (IsDualPurposeRoute(method, segments))
        {
            bool hasJwt = (context.User?.Identity?.IsAuthenticated == true && !context.User.HasClaim("u", "svc")) ||
                          !string.IsNullOrWhiteSpace(context.Request.Headers.Authorization) ||
                          (context.Request.Cookies.TryGetValue("accessToken", out string? c) && !string.IsNullOrWhiteSpace(c));

            if (!hasJwt)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                var problem = new ProblemDetails
                {
                    Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                    Title = "Unauthorized",
                    Status = StatusCodes.Status401Unauthorized,
                    Detail = "User JWT is required to access this endpoint via Gateway.",
                    Instance = context.Request.Path
                };
                await context.Response.WriteAsJsonAsync(problem);
                return;
            }
        }

        await _next(context);
    }

    public static bool IsServiceOnlyRoute(string method, string[] segments)
    {
        // POST /api/v1/invoices
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 3 &&
            segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("invoices", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // POST /api/v1/invoices/{invoiceRef}/cancel
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 5 &&
            segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("invoices", StringComparison.OrdinalIgnoreCase) &&
            segments[4].Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // POST /api/v1/settlements
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 3 &&
            segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("settlements", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // POST /api/v1/payment/verifications
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 4 &&
            segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("payment", StringComparison.OrdinalIgnoreCase) &&
            segments[3].Equals("verifications", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // POST /api/v1/inventory/equipment-listings/batch
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 5 &&
            segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("inventory", StringComparison.OrdinalIgnoreCase) &&
            segments[3].Equals("equipment-listings", StringComparison.OrdinalIgnoreCase) &&
            segments[4].Equals("batch", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // POST /api/v1/inventory/assets/{assetId}/holds
        // POST /api/v1/inventory/equipment-listings/{listingId}/holds
        // POST /api/v1/inventory/allocations/{allocationId}/allocate
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 6 &&
            segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("inventory", StringComparison.OrdinalIgnoreCase))
        {
            if (segments[3].Equals("assets", StringComparison.OrdinalIgnoreCase) && segments[5].Equals("holds", StringComparison.OrdinalIgnoreCase))
                return true;
            if (segments[3].Equals("equipment-listings", StringComparison.OrdinalIgnoreCase) && segments[5].Equals("holds", StringComparison.OrdinalIgnoreCase))
                return true;
            if (segments[3].Equals("allocations", StringComparison.OrdinalIgnoreCase) && segments[5].Equals("allocate", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Media internal service APIs:
        // GET /api/v1/media/{mediaId}/verify
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 5 &&
            segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("media", StringComparison.OrdinalIgnoreCase) &&
            segments[4].Equals("verify", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // GET /api/v1/media/{mediaId} (metadata only, 4 segments, NOT /content which has 5 segments)
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 4 &&
            segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("media", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // POST /api/v1/media/{mediaId}/access
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 5 &&
            segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("media", StringComparison.OrdinalIgnoreCase) &&
            segments[4].Equals("access", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // PUT /api/v1/media/{mediaId}/visibility
        if (string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 5 &&
            segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("media", StringComparison.OrdinalIgnoreCase) &&
            segments[4].Equals("visibility", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Integrity internal APIs: /internal/v1/integrity/*
        if (segments.Length >= 4 &&
            segments[0].Equals("internal", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("integrity", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static bool IsDualPurposeRoute(string method, string[] segments)
    {
        // GET /api/v1/payment/history
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 4 &&
            segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("payment", StringComparison.OrdinalIgnoreCase) &&
            segments[3].Equals("history", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // GET /api/v1/inventory/equipment-listings/{id}/availability
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 6 &&
            segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("inventory", StringComparison.OrdinalIgnoreCase) &&
            segments[3].Equals("equipment-listings", StringComparison.OrdinalIgnoreCase) &&
            segments[5].Equals("availability", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // GET /api/v1/inventory/availability/busy-ranges
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 5 &&
            segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("inventory", StringComparison.OrdinalIgnoreCase) &&
            segments[3].Equals("availability", StringComparison.OrdinalIgnoreCase) &&
            segments[4].Equals("busy-ranges", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
