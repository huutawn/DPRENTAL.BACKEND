using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Domain.IRepositories;

public interface IOperationReceiptRepository
{
    Task<RentalOperationReceipt?> GetByOperationIdAsync(Guid orderId, string operationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RentalOperationReceipt>> GetPendingOperationsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default);

    Task AddAsync(RentalOperationReceipt receipt, CancellationToken cancellationToken = default);

    Task UpdateAsync(RentalOperationReceipt receipt, CancellationToken cancellationToken = default);
}
