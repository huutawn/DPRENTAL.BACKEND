using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Common.Enums;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

/// <summary>
/// EF Core implementation of IRentalOrderRepository.
/// </summary>
public sealed class RentalOrderRepository : IRentalOrderRepository
{
    private readonly RentalServiceDbContext _dbContext;

    public RentalOrderRepository(RentalServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<RentalOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .Include(o => o.TermsAcceptance)
            .Include(o => o.Items)
                .ThenInclude(i => i.UnitAssignments)
            .Include(o => o.Items)
                .ThenInclude(i => i.HandoverRecord)
            .Include(o => o.Items)
                .ThenInclude(i => i.ReturnRecords)
            .Include(o => o.Items)
                .ThenInclude(i => i.DeductionProposal!)
                    .ThenInclude(p => p.Dispute)
            .Include(o => o.StateTransitions)
            .Include(o => o.OperationReceipts)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
    }

    public async Task<RentalOrder?> GetByOrderCodeAsync(string orderCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderCode)) return null;

        return await _dbContext.RentalOrders
            .Include(o => o.TermsAcceptance)
            .Include(o => o.Items)
                .ThenInclude(i => i.UnitAssignments)
            .Include(o => o.Items)
                .ThenInclude(i => i.HandoverRecord)
            .Include(o => o.Items)
                .ThenInclude(i => i.ReturnRecords)
            .Include(o => o.Items)
                .ThenInclude(i => i.DeductionProposal!)
                    .ThenInclude(p => p.Dispute)
            .Include(o => o.StateTransitions)
            .Include(o => o.OperationReceipts)
            .FirstOrDefaultAsync(o => o.OrderCode == orderCode.Trim(), cancellationToken);
    }

    public async Task<RentalOrder?> GetByCheckoutOperationIdAsync(string operationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return null;

        return await _dbContext.RentalOrders
            .Include(o => o.TermsAcceptance)
            .Include(o => o.Items)
                .ThenInclude(i => i.UnitAssignments)
            .Include(o => o.StateTransitions)
            .Include(o => o.OperationReceipts)
            .FirstOrDefaultAsync(o => o.CheckoutOperationId == operationId.Trim(), cancellationToken);
    }

    public async Task<IReadOnlyList<RentalOrder>> GetByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .Include(o => o.Items)
            .Where(o => o.RenterId == renterId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<RentalOrder?> GetPendingPaymentByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .Where(o => o.RenterId == renterId && o.Status == RentalOrderStatus.PendingPayment)
            .OrderByDescending(o => o.PaymentDeadline)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RentalOrder>> GetBySupplierIdAsync(Guid supplierId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .Include(o => o.Items)
            .Where(o => o.Items.Any(i => i.SupplierId == supplierId))
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<RentalOrder> CreateAsync(RentalOrder order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        await _dbContext.RentalOrders.AddAsync(order, cancellationToken);
        return order;
    }

    public Task<bool> UpdateAsync(RentalOrder order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        bool autoDetectChanges = _dbContext.ChangeTracker.AutoDetectChangesEnabled;
        _dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var entry = _dbContext.Entry(order);
            if (entry.State == EntityState.Detached)
            {
                // Preserve the existing detached-aggregate update behavior.
                _dbContext.RentalOrders.Update(order);
                return Task.FromResult(true);
            }
            // Register new children before DetectChanges discovers their non-empty GUIDs.
            foreach (var record in order.Items.SelectMany(i => i.ReturnRecords))
            {
                if (_dbContext.Entry(record).State == EntityState.Detached)
                    _dbContext.Entry(record).State = EntityState.Added;
            }
            foreach (var receipt in order.OperationReceipts)
            {
                if (_dbContext.Entry(receipt).State == EntityState.Detached)
                    _dbContext.Entry(receipt).State = EntityState.Added;
            }
            foreach (var transition in order.StateTransitions)
            {
                if (_dbContext.Entry(transition).State == EntityState.Detached)
                    _dbContext.Entry(transition).State = EntityState.Added;
            }
            return Task.FromResult(true);
        }
        finally
        {
            _dbContext.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }

    public Task<RentalOrder?> GetByIdWithDetailsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return GetByIdAsync(id, cancellationToken);
    }

    public async Task<Guid?> GetOrderIdByItemIdAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var item = await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Id == itemId)
            .Select(i => new { i.RentalOrderId })
            .FirstOrDefaultAsync(cancellationToken);

        return item?.RentalOrderId;
    }

    public async Task<RentalOrder?> GetByItemIdWithDetailsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var orderHeader = await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Id == itemId)
            .Select(i => new { i.RentalOrderId })
            .FirstOrDefaultAsync(cancellationToken);

        if (orderHeader == null)
        {
            return null;
        }

        return await GetByIdAsync(orderHeader.RentalOrderId, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetPendingPaymentExpiredOrderIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .AsNoTracking()
            .Where(o => o.Status == RentalOrderStatus.PendingPayment && o.PaymentDeadline <= utcNow)
            .OrderBy(o => o.PaymentDeadline)
            .Select(o => o.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetPendingSupplierTimeoutItemIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Status == RentalItemStatus.PendingSupplierConfirmation &&
                        i.SupplierConfirmationDeadline.HasValue &&
                        i.SupplierConfirmationDeadline.Value <= utcNow)
            .OrderBy(i => i.SupplierConfirmationDeadline)
            .Select(i => i.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }


    public async Task<IReadOnlyList<RenterCalendarItemRecord>> GetRenterCalendarItemsAsync(
        Guid renterId,
        DateOnly monthStart,
        DateOnly monthEnd,
        IReadOnlyCollection<Dprental.RentalService.Common.Enums.RentalItemStatus> targetStatuses,
        CancellationToken cancellationToken = default)
    {
        var query = from order in _dbContext.RentalOrders.AsNoTracking()
                    where order.RenterId == renterId
                    from item in order.Items
                    where targetStatuses.Contains(item.Status)
                          && item.ExpectedStartDate <= monthEnd
                          && item.ExpectedEndDate > monthStart
                    select new RenterCalendarItemRecord(
                        order.Id,
                        order.OrderCode,
                        item.Id,
                        item.EquipmentListingId,
                        item.ListingTitleSnapshot,
                        item.Quantity,
                        item.ExpectedStartDate,
                        item.ExpectedEndDate,
                        item.Status,
                        order.CreatedAt);

        return await query.ToListAsync(cancellationToken);
    }

    public async Task<SupplierConfirmationStatisticsRecord> GetSupplierConfirmationStatisticsAsync(
        Guid supplierId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        const int supplierRole = (int)ActorRole.Supplier;
        const int systemRole = (int)ActorRole.System;

        var result = await _dbContext.Database.SqlQuery<SupplierConfirmationStatisticsRecord>($"""
            WITH item_accepted AS (
                SELECT t."RentalItemId" AS item_id, MIN(t."OccurredAt") AS first_occurred_at
                FROM rental.rental_state_transitions t
                INNER JOIN rental.rental_items i ON t."RentalItemId" = i."Id"
                WHERE i."SupplierId" = {supplierId}
                  AND t."Action" = 'SupplierAccept'
                  AND t."FromState" = 'PendingSupplierConfirmation'
                  AND t."ToState" = 'AwaitingHandover'
                  AND t."ActorRole" = {supplierRole}
                  AND t."ActorId" = i."SupplierId"
                GROUP BY t."RentalItemId"
            ),
            item_rejected AS (
                SELECT t."RentalItemId" AS item_id, MIN(t."OccurredAt") AS first_occurred_at
                FROM rental.rental_state_transitions t
                INNER JOIN rental.rental_items i ON t."RentalItemId" = i."Id"
                WHERE i."SupplierId" = {supplierId}
                  AND t."Action" = 'SupplierReject'
                  AND t."FromState" = 'PendingSupplierConfirmation'
                  AND t."ToState" = 'Rejected'
                  AND t."ActorRole" = {supplierRole}
                  AND t."ActorId" = i."SupplierId"
                GROUP BY t."RentalItemId"
            ),
            item_timeout AS (
                SELECT t."RentalItemId" AS item_id, MIN(t."OccurredAt") AS first_occurred_at
                FROM rental.rental_state_transitions t
                INNER JOIN rental.rental_items i ON t."RentalItemId" = i."Id"
                WHERE i."SupplierId" = {supplierId}
                  AND t."Action" = 'SupplierTimeout'
                  AND t."FromState" = 'PendingSupplierConfirmation'
                  AND t."ToState" = 'Expired'
                  AND t."ActorRole" = {systemRole}
                GROUP BY t."RentalItemId"
            ),
            item_cancel AS (
                SELECT t."RentalItemId" AS item_id, MIN(t."OccurredAt") AS first_occurred_at
                FROM rental.rental_state_transitions t
                INNER JOIN rental.rental_items i ON t."RentalItemId" = i."Id"
                WHERE i."SupplierId" = {supplierId}
                  AND t."ToState" = 'Cancelled'
                  AND (t."FromState" IS NULL OR t."FromState" <> 'Cancelled')
                  AND t."ActorRole" = {supplierRole}
                  AND t."ActorId" = i."SupplierId"
                  AND EXISTS (
                      SELECT 1
                      FROM rental.rental_state_transitions a
                      WHERE a."RentalItemId" = t."RentalItemId"
                        AND a."Action" = 'SupplierAccept'
                        AND a."FromState" = 'PendingSupplierConfirmation'
                        AND a."ToState" = 'AwaitingHandover'
                        AND a."ActorRole" = {supplierRole}
                        AND a."ActorId" = i."SupplierId"
                        AND a."OccurredAt" <= t."OccurredAt"
                  )
                GROUP BY t."RentalItemId"
            )
            SELECT
                (SELECT COUNT(*)::bigint FROM item_accepted WHERE first_occurred_at >= {fromUtc} AND first_occurred_at < {toUtc}) AS "AcceptedCount",
                (SELECT COUNT(*)::bigint FROM item_rejected WHERE first_occurred_at >= {fromUtc} AND first_occurred_at < {toUtc}) AS "RejectedCount",
                (SELECT COUNT(*)::bigint FROM item_timeout WHERE first_occurred_at >= {fromUtc} AND first_occurred_at < {toUtc}) AS "ConfirmationTimeoutCount",
                (SELECT COUNT(*)::bigint FROM item_cancel WHERE first_occurred_at >= {fromUtc} AND first_occurred_at < {toUtc}) AS "CancelAfterAcceptCount"
        """).FirstOrDefaultAsync(cancellationToken);

        return result ?? new SupplierConfirmationStatisticsRecord(0, 0, 0, 0);
    }

    public async Task<RentalSubOrder?> GetSubOrderByIdAsync(Guid subOrderId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalSubOrders
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == subOrderId, cancellationToken);
    }

    public async Task<RecordPage<RenterOrderSummaryRecord>> GetRenterOrderPageAsync(
        Guid renterId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        IQueryable<RentalOrder> query = _dbContext.RentalOrders
            .AsNoTracking()
            .Where(o => o.RenterId == renterId);

        int totalCount = await query.CountAsync(cancellationToken);
        List<RenterOrderSummaryRecord> items = await query
            .OrderByDescending(o => o.CreatedAt)
            .ThenBy(o => o.Id)
            .Skip(skip)
            .Take(take)
            .Select(o => new RenterOrderSummaryRecord(
                o.Id,
                o.OrderCode,
                o.Status,
                o.Currency,
                o.Items.Sum(i => i.RentalAmount),
                o.Items.Sum(i => i.DepositAmount),
                o.Items.Count,
                o.SubOrders.Count,
                o.PaymentDeadline,
                o.CreatedAt))
            .ToListAsync(cancellationToken);

        return new RecordPage<RenterOrderSummaryRecord>(items, totalCount);
    }

    public async Task<RentalOrder?> GetRenterOrderWithSubOrdersAsync(
        Guid orderId,
        Guid renterId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .AsNoTracking()
            .Include(o => o.SubOrders)
                .ThenInclude(s => s.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.RenterId == renterId, cancellationToken);
    }

    public async Task<RecordPage<SupplierSubOrderSummaryRecord>> GetPaidSubOrderPageForSupplierAsync(
        Guid supplierId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        IQueryable<RentalSubOrder> query = PaidSubOrdersOf(supplierId);

        int totalCount = await query.CountAsync(cancellationToken);
        List<SupplierSubOrderSummaryRecord> items = await query
            .OrderByDescending(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .Skip(skip)
            .Take(take)
            .Select(s => new SupplierSubOrderSummaryRecord(
                s.Id,
                s.SubOrderCode,
                s.RentalOrder!.OrderCode,
                s.RentalOrder.Currency,
                s.Items.Sum(i => i.RentalAmount),
                s.Items.Sum(i => i.DepositAmount),
                s.Items.Select(i => i.Status).ToList(),
                s.RentalOrder.PaymentConfirmedAt,
                s.CreatedAt))
            .ToListAsync(cancellationToken);

        return new RecordPage<SupplierSubOrderSummaryRecord>(items, totalCount);
    }

    public async Task<RentalSubOrder?> GetPaidSubOrderForSupplierAsync(
        Guid subOrderId,
        Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        return await PaidSubOrdersOf(supplierId)
            .Include(s => s.RentalOrder)
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.Id == subOrderId, cancellationToken);
    }

    // Nhà cung cấp chỉ thấy đơn sau khi người thuê đã thanh toán đúng hạn; thanh toán trễ sẽ được hoàn tiền.
    private IQueryable<RentalSubOrder> PaidSubOrdersOf(Guid supplierId) =>
        _dbContext.RentalSubOrders
            .AsNoTracking()
            .Where(s => s.SupplierId == supplierId &&
                        s.RentalOrder!.PaymentConfirmedAt != null &&
                        !s.RentalOrder.LatePaymentRefundRequired);
}
