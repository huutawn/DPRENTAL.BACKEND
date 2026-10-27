namespace Dprental.AnalyticsService.Application.Kafka.Consumers;

/// <summary>Kết quả xử lý một message. Worker commit offset cho mọi kết quả trừ <see cref="RetryLater"/>.</summary>
public enum DispatchOutcome
{
    Processed,    // Đã lưu read model (+ inbox)
    Duplicate,    // Đã xử lý trước đó: Kafka giao lại hoặc Payment phát lại
    Skipped,      // Topic / loại event Analytics không dùng
    DeadLettered, // Lỗi dữ liệu: đã ghi dead letter, đi tiếp
    RetryLater    // Lỗi tạm thời: KHÔNG commit offset, thử lại chính message này
}

/// <summary>Kết quả kèm loại event và mã lỗi, dùng cho log và thông báo SignalR.</summary>
public sealed record DispatchResult(DispatchOutcome Outcome, string? MessageType = null, string? ErrorCode = null)
{
    public bool ShouldCommitOffset => Outcome != DispatchOutcome.RetryLater;
}
