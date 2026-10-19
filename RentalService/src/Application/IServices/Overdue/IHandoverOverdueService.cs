namespace Dprental.RentalService.Application.IServices.Overdue;

public sealed record HandoverOverdueResult(int ProcessedItems, int AutoCancelled, int NoticesSent);

public interface IHandoverOverdueService
{
    Task<IReadOnlyList<Guid>> GetDueOrderIdsAsync(int batchSize, CancellationToken cancellationToken = default);

    Task<HandoverOverdueResult> ProcessOrderAsync(Guid orderId, CancellationToken cancellationToken = default);
}
