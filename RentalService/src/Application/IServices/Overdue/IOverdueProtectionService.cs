namespace Dprental.RentalService.Application.IServices.Overdue;

public interface IOverdueProtectionService
{
    Task<IReadOnlyList<Guid>> GetDueOrderIdsAsync(int batchSize, CancellationToken cancellationToken = default);

    Task<OverdueOrderResult> ProcessOrderAsync(Guid orderId, CancellationToken cancellationToken = default);
}

public sealed record OverdueOrderResult(int Scheduled, int Succeeded, int Failed);

public sealed record OverdueProtectionBatchResult(
    int OrdersScanned, int OperationsScheduled, int OperationsSucceeded,
    int OperationsFailed, int OrdersSkipped);