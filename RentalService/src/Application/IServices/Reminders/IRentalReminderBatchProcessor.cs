using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.IServices.Reminders;

public sealed record ProcessOrderGroupResult(int Processed, int NoticesSent, int Completed, int Failed);

public interface IRentalReminderBatchProcessor
{
    Task<ProcessOrderGroupResult> ProcessOrderReminderGroupAsync(
        Guid orderId,
        IReadOnlyList<ReminderClaimDescriptor> descriptors,
        CancellationToken cancellationToken = default);
}
