using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.IServices.Reminders;

public enum ReminderItemProcessOutcome
{
    Succeeded,
    Deferred,
    LeaseLost,
    RetryableFailure,
    PermanentFailure
}

public sealed record ReminderItemProcessResult(
    Guid ScheduleId,
    Guid LeaseToken,
    ReminderItemProcessOutcome Outcome,
    string? ErrorReason = null);

/// <summary>
/// Kết quả xử lý một nhóm schedule thuộc cùng một Order.
/// Processed: Số lượng claim descriptors đã được xét trong nhóm (không phải số thay đổi đã commit).
/// NoticesSent: Số thông báo outbox đã gửi và commit thành công (0 nếu transaction rollback).
/// Completed: Số lịch nhắc đã hoàn tất và commit thành công (0 nếu transaction rollback).
/// Failed: Số lịch nhắc thất bại (RetryableFailure hoặc PermanentFailure).
/// ItemResults: Danh sách kết quả chi tiết cho từng descriptor. Mỗi descriptor xuất hiện đúng một lần.
/// </summary>
public sealed record ProcessOrderGroupResult(
    int Processed,
    int NoticesSent,
    int Completed,
    int Failed,
    IReadOnlyList<ReminderItemProcessResult> ItemResults)
{
    public ProcessOrderGroupResult(int processed, int noticesSent, int completed, int failed)
        : this(processed, noticesSent, completed, failed, Array.Empty<ReminderItemProcessResult>())
    {
    }
}

public interface IRentalReminderBatchProcessor
{
    Task<ProcessOrderGroupResult> ProcessOrderReminderGroupAsync(
        Guid orderId,
        IReadOnlyList<ReminderClaimDescriptor> descriptors,
        CancellationToken cancellationToken = default);
}
