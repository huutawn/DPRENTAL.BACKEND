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

    Task<RentalOrder?> GetPendingPaymentByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RentalOrder>> GetBySupplierIdAsync(Guid supplierId, CancellationToken cancellationToken = default);

    Task<RentalOrder> CreateAsync(RentalOrder order, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(RentalOrder order, CancellationToken cancellationToken = default);

    Task<RentalOrder?> GetByIdWithDetailsAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RentalOrder?> GetByItemIdWithDetailsAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<Guid?> GetOrderIdByItemIdAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetPendingPaymentExpiredOrderIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetPendingSupplierTimeoutItemIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetOverdueUnprotectedOrderIdsAsync(
    DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetDeductionTimeoutItemIdsAsync(
    DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RenterCalendarItemRecord>> GetRenterCalendarItemsAsync(
        Guid renterId,
        DateOnly monthStart,
        DateOnly monthEnd,
        IReadOnlyCollection<Dprental.RentalService.Common.Enums.RentalItemStatus> targetStatuses,
        CancellationToken cancellationToken = default);

    Task<SupplierConfirmationStatisticsRecord> GetSupplierConfirmationStatisticsAsync(
        Guid supplierId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);

}
