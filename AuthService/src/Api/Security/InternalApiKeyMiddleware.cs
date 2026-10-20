using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Dprental.AuthService.Api.Security;

/// <summary>
/// Middleware xác thực header X-Api-Key cho mọi request nội bộ trước authentication/authorization.
/// </summary>
public sealed class InternalApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[] _configuredKeyBytes;
    private readonly string[] _excludedPrefixes;

    /// <summary>
    /// Khởi tạo middleware xác thực API key nội bộ.
    /// </summary>
    public InternalApiKeyMiddleware(RequestDelegate next, IConfiguration configuration, string[]? additionalExclusions = null)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        string? key = configuration["InternalAuthentication:ApiKey"]
            ?? configuration["INTERNAL_API_KEY"];

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("InternalAuthentication:ApiKey must be configured.");
        }

        _configuredKeyBytes = Encoding.UTF8.GetBytes(key.Trim());

        var defaultExclusions = new[]
        {
            "/health",
            "/healthz",
            "/ready",
            "/live",
            "/_health",
            "/.well-known",
            "/swagger",
            "/openapi"
        };

        _excludedPrefixes = additionalExclusions == null
            ? defaultExclusions
            : defaultExclusions.Concat(additionalExclusions).ToArray();
    }

    /// <summary>
    /// Thực thi kiểm tra header X-Api-Key.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        PathString path = context.Request.Path;
        foreach (string prefix in _excludedPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }
        }

        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var headerValues) ||
            headerValues.Count != 1 ||
            string.IsNullOrWhiteSpace(headerValues[0]))
        {
            await ReturnUnauthorizedAsync(context, "Header 'X-Api-Key' is missing or has multiple values.");
            return;
        }

        byte[] providedBytes = Encoding.UTF8.GetBytes(headerValues[0]!);
        if (providedBytes.Length != _configuredKeyBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(providedBytes, _configuredKeyBytes))
        {
            await ReturnUnauthorizedAsync(context, "Invalid 'X-Api-Key' header.");
            return;
        }

        context.Items["InternalApiKeyVerified"] = true;
        await _next(context);
    }
    private static async Task ReturnUnauthorizedAsync(HttpContext context, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";
        var problem = new ProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
            Title = "Unauthorized",
            Status = StatusCodes.Status401Unauthorized,
            Detail = detail,
            Instance = context.Request.Path
        };
        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }
}
