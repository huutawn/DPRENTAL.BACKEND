namespace Dprental.RentalService.Application.IServices.Returns;

public sealed record ReturnConfirmationOverdueBatchResult(int ProcessedOrders, int WarnedUnits, int FailedOrders);

public interface IReturnConfirmationOverdueService
{
    Task<IReadOnlyList<Guid>> GetDueOrderIdsAsync(int batchSize, CancellationToken cancellationToken = default);
    Task<int> ProcessOrderAsync(Guid orderId, CancellationToken cancellationToken = default);
    Task<ReturnConfirmationOverdueBatchResult> ProcessBatchAsync(int batchSize = 100, CancellationToken cancellationToken = default);
}
