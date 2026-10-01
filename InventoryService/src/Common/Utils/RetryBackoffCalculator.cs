namespace Dprental.InventoryService.Common.Utils;

/// <summary>
/// Tính toán thời gian chờ theo exponential backoff từ 1 giây, trần 60 giây, jitter 20%.
/// </summary>
public static class RetryBackoffCalculator
{
    public static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(60);
    public const double DefaultJitterRatio = 0.20; // 20%

    /// <summary>
    /// Tính khoảng thời gian trễ cho lần thử lại thứ attempt (1-based index).
    /// </summary>
    /// <param name="attempt">Số lần thử lại hiện tại (1, 2, 3...).</param>
    /// <param name="customRandomFraction">Giá trị ngẫu nhiên từ -1.0 đến 1.0 phục vụ deterministic unit tests.</param>
    /// <returns>Khoảng thời gian trễ TimeSpan.</returns>
    public static TimeSpan CalculateDelay(int attempt, double? customRandomFraction = null)
    {
        if (attempt <= 1)
        {
            var baseSec = BaseInterval.TotalSeconds;
            var fraction = customRandomFraction ?? (Random.Shared.NextDouble() * 2.0 - 1.0);
            var jitterSec = baseSec * DefaultJitterRatio * fraction;
            return TimeSpan.FromSeconds(Math.Max(0.5, baseSec + jitterSec));
        }

        // 2^(attempt - 1)
        var exponentialSeconds = Math.Pow(2, attempt - 1);
        var cappedSeconds = Math.Min(MaxInterval.TotalSeconds, exponentialSeconds);

        var randomFraction = customRandomFraction ?? (Random.Shared.NextDouble() * 2.0 - 1.0);
        var jitter = cappedSeconds * DefaultJitterRatio * randomFraction;
        var finalSeconds = Math.Max(0.5, Math.Min(MaxInterval.TotalSeconds * (1.0 + DefaultJitterRatio), cappedSeconds + jitter));

        return TimeSpan.FromSeconds(finalSeconds);
    }

    /// <summary>
    /// Tính thời điểm thực thi tiếp theo dựa trên thời điểm hiện tại và số lần thử lại.
    /// </summary>
    public static DateTimeOffset CalculateNextAttemptAt(DateTimeOffset now, int attempt, double? customRandomFraction = null)
    {
        return now.Add(CalculateDelay(attempt, customRandomFraction));
    }
}
