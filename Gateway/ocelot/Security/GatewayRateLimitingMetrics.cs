using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Dprental.Gateway.Security;

/// <summary>
/// Thu thập metrics cho rate limiting gateway mà không ghi nhận bất kỳ thông tin nhạy cảm nào (token, user ID, IP).
/// Theo dõi số lần chặn theo nhóm đọc/ghi và loại partition.
/// </summary>
public static class GatewayRateLimitingMetrics
{
    public const string MeterName = "Dprental.Gateway.RateLimiting";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> BlockedRequestsCounter = Meter.CreateCounter<long>(
        name: "gateway.rate_limiting.blocked_requests",
        unit: "{request}",
        description: "Số lượng request bị chặn bởi rate limiter của Gateway theo nhóm đọc/ghi và loại partition.");

    /// <summary>
    /// Ghi nhận một lần chặn request do vượt hạn mức.
    /// Nhãn chỉ bao gồm request_group và partition_type.
    /// </summary>
    /// <param name="requestGroup">Nhóm request ("read" hoặc "write").</param>
    /// <param name="partitionType">Loại partition ("user" hoặc "ip").</param>
    public static void RecordBlockedRequest(string requestGroup, string partitionType)
    {
        BlockedRequestsCounter.Add(1,
            new KeyValuePair<string, object?>("request_group", requestGroup),
            new KeyValuePair<string, object?>("partition_type", partitionType));
    }
}
