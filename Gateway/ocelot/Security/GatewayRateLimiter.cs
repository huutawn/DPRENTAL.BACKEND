using System;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dprental.Gateway.Security;

/// <summary>
/// Cài đặt chính sách kiểm soát tần suất request (Rate Limiting) của Gateway.
/// Tái sử dụng PartitionedRateLimiter và sliding window của .NET, quản lý bộ đếm trong từng instance Gateway.
/// Phân chia hạn mức riêng cho đọc và ghi, ưu tiên danh tính user đã xác thực và dự phòng bằng IP.
/// </summary>
public sealed class GatewayRateLimiter : IGatewayRateLimiter, IDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> partitionedRateLimiter;
    private readonly IdentityExtractor identityExtractor;
    private readonly GatewayRateLimitingOptions options;
    private bool disposed;

    /// <summary>
    /// Khởi tạo GatewayRateLimiter với cấu hình bảo mật và bộ trích xuất danh tính.
    /// </summary>
    /// <param name="securityOptions">Cấu hình bảo mật Gateway chứa các hạn mức rate limit.</param>
    /// <param name="identityExtractor">Bộ trích xuất danh tính từ ClaimsPrincipal đã xác thực.</param>
    public GatewayRateLimiter(
        IOptions<GatewaySecurityOptions> securityOptions,
        IdentityExtractor identityExtractor)
    {
        ArgumentNullException.ThrowIfNull(securityOptions);
        this.identityExtractor = identityExtractor ?? throw new ArgumentNullException(nameof(identityExtractor));
        options = securityOptions.Value.RateLimiting ?? new GatewayRateLimitingOptions();

        partitionedRateLimiter = PartitionedRateLimiter.Create<HttpContext, RateLimitPartitionKey>(context =>
        {
            if (IsExempt(context))
            {
                return RateLimitPartition.GetNoLimiter<RateLimitPartitionKey>(default);
            }

            RateLimitPartitionKey key = ResolvePartitionKeyInternal(context);
            return RateLimitPartition.GetSlidingWindowLimiter(key, partitionKey =>
            {
                int permitLimit = GetPermitLimit(partitionKey);
                return new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    SegmentsPerWindow = options.SegmentsPerWindow,
                    QueueLimit = 0,
                    AutoReplenishment = true
                };
            });
        });
    }

    /// <inheritdoc />
    public GatewayRateLimitResult CheckRateLimit(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsExempt(context))
        {
            return GatewayRateLimitResult.Allowed;
        }

        RateLimitPartitionKey partitionKey = ResolvePartitionKeyInternal(context);

        using RateLimitLease lease = partitionedRateLimiter.AttemptAcquire(context);
        if (lease.IsAcquired)
        {
            return GatewayRateLimitResult.Allowed;
        }

        int retryAfterSeconds = 1;
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfterTimeSpan) &&
            retryAfterTimeSpan.TotalSeconds > 0)
        {
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfterTimeSpan.TotalSeconds));
        }

        GatewayRateLimitingMetrics.RecordBlockedRequest(partitionKey.RequestGroup, partitionKey.IdentityType);

        return GatewayRateLimitResult.Blocked(retryAfterSeconds, partitionKey.RequestGroup, partitionKey.IdentityType);
    }

    /// <inheritdoc />
    public bool TryResolvePartitionKey(HttpContext context, out RateLimitPartitionKey partitionKey)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsExempt(context))
        {
            partitionKey = default;
            return false;
        }

        partitionKey = ResolvePartitionKeyInternal(context);
        return true;
    }

    /// <summary>
    /// Kiểm tra xem request có thuộc diện được miễn trừ rate limit hay không (OPTIONS hoặc endpoint health check).
    /// </summary>
    public static bool IsExempt(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            return true;
        }

        PathString path = context.Request.Path;
        return path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/_health", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Chuẩn hóa và xác định địa chỉ IP của client từ Connection.RemoteIpAddress sau khi ForwardedHeaders xử lý.
    /// Chuẩn hóa IPv4-mapped IPv6 và trả về "unknown" nếu thiếu IP. Tuyệt đối không đọc trực tiếp header client.
    /// </summary>
    public static string ResolveClientIpAddress(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IPAddress? remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is null)
        {
            return RateLimitPartitionKey.UnknownIp;
        }

        if (remoteIp.IsIPv4MappedToIPv6)
        {
            remoteIp = remoteIp.MapToIPv4();
        }

        return remoteIp.ToString();
    }

    private RateLimitPartitionKey ResolvePartitionKeyInternal(HttpContext context)
    {
        string requestGroup = (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            ? RateLimitPartitionKey.ReadGroup
            : RateLimitPartitionKey.WriteGroup;

        IdentityContext identity = identityExtractor.Extract(context.User);
        if (identity.IsAuthenticated && !string.IsNullOrWhiteSpace(identity.UserId))
        {
            return new RateLimitPartitionKey(RateLimitPartitionKey.UserType, identity.UserId, requestGroup);
        }

        string clientIp = ResolveClientIpAddress(context);
        return new RateLimitPartitionKey(RateLimitPartitionKey.IpType, clientIp, requestGroup);
    }

    private int GetPermitLimit(RateLimitPartitionKey key) => (key.IdentityType, key.RequestGroup) switch
    {
        (RateLimitPartitionKey.UserType, RateLimitPartitionKey.ReadGroup) => options.UserReadLimit,
        (RateLimitPartitionKey.UserType, RateLimitPartitionKey.WriteGroup) => options.UserWriteLimit,
        (RateLimitPartitionKey.IpType, RateLimitPartitionKey.ReadGroup) => options.IpReadLimit,
        (RateLimitPartitionKey.IpType, RateLimitPartitionKey.WriteGroup) => options.IpWriteLimit,
        _ => options.IpWriteLimit
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (!disposed)
        {
            partitionedRateLimiter.Dispose();
            disposed = true;
        }
    }
}
