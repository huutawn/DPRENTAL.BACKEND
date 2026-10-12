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
        var entry = _dbContext.Entry(order);
        if (entry.State == EntityState.Detached)
        {
            _dbContext.RentalOrders.Update(order);
        }

        foreach (var t in order.StateTransitions)
        {
            var tEntry = _dbContext.Entry(t);
            if (tEntry.State == EntityState.Detached)
            {
                tEntry.State = EntityState.Added;
            }
        }

        return Task.FromResult(true);
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
}
