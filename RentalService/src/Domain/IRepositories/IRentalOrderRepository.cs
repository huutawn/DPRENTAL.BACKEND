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
        DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

    Task<IReadOnlyList<Guid>> GetDeductionTimeoutItemIdsAsync(
        DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

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

    /// <summary>Đơn con theo mã, không kèm dòng; không theo dõi thay đổi.</summary>
    Task<RentalSubOrder?> GetSubOrderByIdAsync(Guid subOrderId, CancellationToken cancellationToken = default);

    /// <summary>Một trang đơn của người thuê, mới nhất trước.</summary>
    Task<RecordPage<RenterOrderSummaryRecord>> GetRenterOrderPageAsync(
        Guid renterId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>Đơn kèm các đơn con và dòng, chỉ khi đơn thuộc người thuê này; không theo dõi thay đổi.</summary>
    Task<RentalOrder?> GetRenterOrderWithSubOrdersAsync(
        Guid orderId,
        Guid renterId,
        CancellationToken cancellationToken = default);

    /// <summary>Một trang đơn con của nhà cung cấp, chỉ gồm đơn đã thanh toán đúng hạn, mới nhất trước.</summary>
    Task<RecordPage<SupplierSubOrderSummaryRecord>> GetPaidSubOrderPageForSupplierAsync(
        Guid supplierId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đơn con kèm dòng và đơn cha, chỉ khi đơn con thuộc nhà cung cấp này và đơn đã thanh toán đúng hạn;
    /// không theo dõi thay đổi.
    /// </summary>
    Task<RentalSubOrder?> GetPaidSubOrderForSupplierAsync(
        Guid subOrderId,
        Guid supplierId,
        CancellationToken cancellationToken = default);
}
