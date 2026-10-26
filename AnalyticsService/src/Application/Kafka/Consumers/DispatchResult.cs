namespace Dprental.AnalyticsService.Application.Kafka.Consumers;

/// <summary>
/// Kết quả xử lý một message. Worker commit offset cho mọi kết quả trừ <see cref="RetryLater"/>.
/// </summary>
public enum DispatchOutcome
{
    Processed,    // Đã lưu facts + inbox
    Duplicate,    // messageId đã có trong inbox: Kafka gửi lại
    Skipped,      // Loại event Analytics không dùng
    DeadLettered, // Lỗi dữ liệu: đã ghi dead letter, đi tiếp
    RetryLater    // Lỗi tạm thời (DB): KHÔNG commit offset, thử lại chính message này
}

/// <summary>
/// Kết quả kèm loại event và mã lỗi (nếu có), dùng cho log và cho thông báo SignalR sau này (B8).
/// </summary>
public sealed record DispatchResult(DispatchOutcome Outcome, string? MessageType = null, string? ErrorCode = null)
{
    public bool ShouldCommitOffset => Outcome != DispatchOutcome.RetryLater;
}
