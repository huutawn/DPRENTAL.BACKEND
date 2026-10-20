using Microsoft.AspNetCore.Http;

namespace Dprental.Gateway.Security;

/// <summary>
/// Dịch vụ kiểm soát tần suất request (Rate Limiting) theo User ID và IP cho API Gateway.
/// </summary>
public interface IGatewayRateLimiter
{
    /// <summary>
    /// Kiểm tra và tiêu hao hạn mức quota cho request hiện tại.
    /// Nếu vượt quota, ghi nhận metric và trả về kết quả bị chặn kèm thời gian Retry-After.
    /// </summary>
    /// <param name="context">Ngữ cảnh HTTP của request.</param>
    /// <returns>Kết quả đánh giá (Allowed hoặc Blocked kèm RetryAfterSeconds).</returns>
    GatewayRateLimitResult CheckRateLimit(HttpContext context);

    /// <summary>
    /// Xác định khóa partition cho request HTTP phục vụ kiểm thử và phân loại danh tính.
    /// </summary>
    /// <param name="context">Ngữ cảnh HTTP của request.</param>
    /// <param name="partitionKey">Khóa partition được tính toán.</param>
    /// <returns>True nếu request chịu quota rate limit; False nếu request được miễn trừ (OPTIONS hoặc health check).</returns>
    bool TryResolvePartitionKey(HttpContext context, out RateLimitPartitionKey partitionKey);
}
