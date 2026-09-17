using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Dprental.Gateway.Application.Traffic;
using Dprental.Gateway.Domain.Security;
using Dprental.Gateway.Domain.Traffic;
using Dprental.Gateway.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Dprental.Gateway.Infrastructure.Traffic.RateLimiting;

/// <summary>
/// Cấu hình và triển khai chính sách giới hạn tần suất (Rate Limiting) cho API Gateway.
/// Hỗ trợ 3 phân tầng chính: Anonymous (theo IP), Authenticated (theo UserId), và Sensitive (cho route nhạy cảm).
/// Tuyệt đối không chứa logic hạn ngạch nghiệp vụ (Domain Business Quota).
/// </summary>
public static class GatewayRateLimitingConfiguration
{
    private static readonly Action<ILogger, string, string, Exception?> LogRateLimitExceeded = LoggerMessage.Define<string, string>(
        LogLevel.Warning,
        new EventId(1, "RateLimitExceeded"),
        "Request từ {PartitionKey} tới đường dẫn {Path} đã vượt quá giới hạn tần suất cho phép (Rate Limit Exceeded).");

    /// <summary>
    /// Cấu hình RateLimiterOptions với bộ phân vùng toàn cục (Global Partitioned Rate Limiter)
    /// và xử lý phản hồi khi bị từ chối (OnRejected) trả về HTTP 429 chuẩn Gateway.
    /// </summary>
    /// <param name="options">Options của ASP.NET Core RateLimiter.</param>
    /// <param name="trafficOptions">Cấu hình điều phối lưu lượng Gateway.</param>
    /// <param name="logger">Logger ghi nhận cảnh báo khi vượt ngưỡng.</param>
    public static void Configure(RateLimiterOptions options, GatewayTrafficOptions trafficOptions, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(trafficOptions);
        ArgumentNullException.ThrowIfNull(logger);

        GatewayRateLimitingOptions rateLimitingOptions = trafficOptions.RateLimiting;

        // Xử lý phản hồi khi vượt quá ngưỡng giới hạn (HTTP 429 Too Many Requests)
        options.OnRejected = async (context, cancellationToken) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            {
                int seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
                context.HttpContext.Response.Headers[ResilienceConstants.RetryAfterHeader] =
                    seconds.ToString(CultureInfo.InvariantCulture);
            }

            LogRateLimitExceeded(
                logger,
                context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                context.HttpContext.Request.Path,
                null);

            await GatewayErrorResponseWriter.WriteAsync(
                context.HttpContext,
                StatusCodes.Status429TooManyRequests,
                GatewayError.RateLimitExceeded,
                cancellationToken);
        };

        if (!rateLimitingOptions.Enabled)
        {
            return;
        }

        // Thiết lập bộ phân vùng tỷ lệ toàn cục (Global Partitioned Limiter)
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            PathString path = httpContext.Request.Path;

            // 1. Phân tầng Sensitive Routes (đăng nhập, tạo session, đổi mật khẩu)
            if (IsSensitiveRoute(path, rateLimitingOptions.SensitivePaths))
            {
                string clientIp = GetClientIpAddress(httpContext);
                string partitionKey = $"sensitive_{clientIp}";

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = rateLimitingOptions.Sensitive.PermitLimit,
                        Window = TimeSpan.FromSeconds(rateLimitingOptions.Sensitive.WindowSeconds),
                        QueueLimit = rateLimitingOptions.Sensitive.QueueLimit,
                        AutoReplenishment = true
                    });
            }

            // 2. Phân tầng Authenticated User (đã đăng nhập, phân vùng theo UserId)
            if (httpContext.User?.Identity is not null && httpContext.User.Identity.IsAuthenticated)
            {
                string? userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
                                 httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ??
                                 httpContext.User.FindFirstValue("uid");

                if (!string.IsNullOrWhiteSpace(userId))
                {
                    string partitionKey = $"auth_{userId}";

                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey,
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = rateLimitingOptions.Authenticated.PermitLimit,
                            Window = TimeSpan.FromSeconds(rateLimitingOptions.Authenticated.WindowSeconds),
                            QueueLimit = rateLimitingOptions.Authenticated.QueueLimit,
                            AutoReplenishment = true
                        });
                }
            }

            // 3. Phân tầng Anonymous User (chưa đăng nhập, phân vùng theo Client IP)
            string ipAddress = GetClientIpAddress(httpContext);
            string anonPartitionKey = $"anon_{ipAddress}";

            return RateLimitPartition.GetFixedWindowLimiter(
                anonPartitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rateLimitingOptions.Anonymous.PermitLimit,
                    Window = TimeSpan.FromSeconds(rateLimitingOptions.Anonymous.WindowSeconds),
                    QueueLimit = rateLimitingOptions.Anonymous.QueueLimit,
                    AutoReplenishment = true
                });
        });
    }

    private static bool IsSensitiveRoute(PathString requestPath, string[] sensitivePaths)
    {
        if (sensitivePaths is null || sensitivePaths.Length == 0)
        {
            return false;
        }

        foreach (string sensitivePath in sensitivePaths)
        {
            if (requestPath.StartsWithSegments(sensitivePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetClientIpAddress(HttpContext context)
    {
        IPAddress? remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is not null)
        {
            return remoteIp.ToString();
        }

        // Dự phòng lấy từ X-Forwarded-For nếu đã được proxy tin cậy xử lý
        if (context.Request.Headers.TryGetValue(GatewayHeaderConstants.ForwardedFor, out var forwardedHeader))
        {
            string? firstIp = forwardedHeader.FirstOrDefault()?.Split(',')[0].Trim();
            if (!string.IsNullOrWhiteSpace(firstIp))
            {
                return firstIp;
            }
        }

        return "127.0.0.1";
    }
}
