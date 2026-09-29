using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Dprental.Gateway.Errors;
using Dprental.Gateway.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Dprental.Gateway.Traffic;

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
            // Thiết lập header Retry-After theo chuẩn HTTP
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            {
                context.HttpContext.Response.Headers[HeaderNames.RetryAfter] =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                // Mặc định retry sau 60 giây nếu limiter không cung cấp metadata chi tiết
                context.HttpContext.Response.Headers[HeaderNames.RetryAfter] = "60";
            }

            LogRateLimitExceeded(logger, context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", context.HttpContext.Request.Path, null);

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
        // 1. Ưu tiên RemoteIpAddress từ socket kết nối (nếu đã qua UseForwardedHeaders hợp lệ, giá trị này chính là IP client thật)
        IPAddress? remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is not null)
        {
            return remoteIp.ToString();
        }

        // 2. Dự phòng đọc header X-Forwarded-For nếu RemoteIpAddress là null
        string? forwardedFor = context.Request.Headers[GatewayHeaderConstants.ForwardedFor].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            string firstIp = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            if (IPAddress.TryParse(firstIp, out _))
            {
                return firstIp;
            }
        }

        return "unknown";
    }
}
