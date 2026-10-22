using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Domain.IRepositories;

public interface IRentalCheckoutOperationRepository
{
    Task<RentalCheckoutOperation?> GetByExpectedOrderIdAsync(Guid expectedOrderId, CancellationToken cancellationToken = default);
    Task<RentalCheckoutOperation?> GetByOperationIdAsync(string operationId, CancellationToken cancellationToken = default);
    Task<RentalCheckoutOperation?> GetByOperationIdForRenterAsync(string operationId, Guid renterUserId, CancellationToken cancellationToken = default);
    Task<RentalCheckoutOperation?> GetActiveOperationByRenterAsync(Guid renterUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RentalCheckoutOperation>> GetTimedOutOperationsAsync(DateTimeOffset now, int limit = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RentalCheckoutOperation>> GetPendingProcessingOperationsAsync(DateTimeOffset now, int limit = 50, CancellationToken cancellationToken = default);
    Task AddAsync(RentalCheckoutOperation operation, CancellationToken cancellationToken = default);
    Task UpdateAsync(RentalCheckoutOperation operation, CancellationToken cancellationToken = default);
}
