using Dprental.RentalService.Common.Enums;
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

    /// <summary>Một trang đơn của người thuê, mới nhất trước; <paramref name="status"/> rỗng là mọi trạng thái.</summary>
    Task<RecordPage<RenterOrderSummaryRecord>> GetRenterOrderPageAsync(
        Guid renterId,
        RentalOrderStatus? status,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đơn kèm các đơn con, dòng, bàn giao, trả máy, đề xuất khấu trừ và các yêu cầu hoàn tiền, chỉ khi đơn thuộc
    /// người thuê này; không theo dõi thay đổi.
    /// </summary>
    Task<RentalOrder?> GetRenterOrderWithSubOrdersAsync(
        Guid orderId,
        Guid renterId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Một trang đơn con của nhà cung cấp, chỉ gồm đơn đã thanh toán đúng hạn, mới nhất trước;
    /// <paramref name="itemStatus"/> có giá trị thì chỉ lấy đơn con có ít nhất một dòng ở trạng thái đó.
    /// </summary>
    Task<RecordPage<SupplierSubOrderSummaryRecord>> GetPaidSubOrderPageForSupplierAsync(
        Guid supplierId,
        RentalItemStatus? itemStatus,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đơn con kèm đơn cha, dòng, máy đã gán, bàn giao, trả máy và đề xuất khấu trừ, chỉ khi đơn con thuộc nhà cung cấp này và đơn đã thanh toán đúng hạn;
    /// không theo dõi thay đổi.
    /// </summary>
    Task<RentalSubOrder?> GetPaidSubOrderForSupplierAsync(
        Guid subOrderId,
        Guid supplierId,
        CancellationToken cancellationToken = default);
}
