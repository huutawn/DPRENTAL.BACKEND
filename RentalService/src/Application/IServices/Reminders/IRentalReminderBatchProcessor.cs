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
