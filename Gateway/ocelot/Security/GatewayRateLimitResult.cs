namespace Dprental.Gateway.Security;

/// <summary>
/// Kết quả đánh giá rate limit cho một HTTP request.
/// </summary>
public readonly record struct GatewayRateLimitResult(
    bool IsAllowed,
    int RetryAfterSeconds,
    string? RequestGroup,
    string? PartitionType)
{
    /// <summary>
    /// Request được phép đi tiếp downstream.
    /// </summary>
    public static GatewayRateLimitResult Allowed { get; } = new(true, 0, null, null);

    /// <summary>
    /// Request bị chặn do vượt quota, chứa số giây cần chờ trước khi thử lại (Retry-After tối thiểu 1).
    /// </summary>
    public static GatewayRateLimitResult Blocked(int retryAfterSeconds, string requestGroup, string partitionType) =>
        new(false, retryAfterSeconds, requestGroup, partitionType);
}
