using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Domain.IRepositories;

/// <summary>
/// Contract repository cho aggregate RentalOrder.
/// </summary>
public interface IRentalOrderRepository
{
    Task<RentalOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RentalOrder?> GetByOrderCodeAsync(string orderCode, CancellationToken cancellationToken = default);

    Task<RentalOrder?> GetByCheckoutOperationIdAsync(string operationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RentalOrder>> GetByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RentalOrder>> GetBySupplierIdAsync(Guid supplierId, CancellationToken cancellationToken = default);

    Task<RentalOrder> CreateAsync(RentalOrder order, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(RentalOrder order, CancellationToken cancellationToken = default);
}
