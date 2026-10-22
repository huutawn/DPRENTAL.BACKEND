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
    private const string RequestRefundAction = "RequestRefund";

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
            else if (entry.State == EntityState.Unchanged)
            {
                entry.State = EntityState.Modified;
            }
            // Register new children before DetectChanges discovers their non-empty GUIDs.
            foreach (var record in order.Items.SelectMany(i => i.ReturnRecords))
            {
                if (_dbContext.Entry(record).State == EntityState.Detached)
                    _dbContext.Entry(record).State = EntityState.Added;
            }
            foreach (var handover in order.Items.Select(i => i.HandoverRecord).Where(h => h is not null))
            {
                if (_dbContext.Entry(handover!).State == EntityState.Detached)
                    _dbContext.Entry(handover!).State = EntityState.Added;
            }
            foreach (var proposal in order.Items.Select(i => i.DeductionProposal).Where(p => p is not null))
            {
                if (_dbContext.Entry(proposal!).State == EntityState.Detached)
                    _dbContext.Entry(proposal!).State = EntityState.Added;
            }
            foreach (var dispute in order.Items.Select(i => i.DeductionProposal?.Dispute).Where(d => d is not null))
            {
                if (_dbContext.Entry(dispute!).State == EntityState.Detached)
                    _dbContext.Entry(dispute!).State = EntityState.Added;
            }
            foreach (var receipt in order.OperationReceipts)
            {
                var rEntry = _dbContext.Entry(receipt);
                if (rEntry.State == EntityState.Detached)
                    rEntry.State = EntityState.Added;
                else if (rEntry.State == EntityState.Unchanged)
                    rEntry.State = EntityState.Modified;
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
    public async Task<RentalOrder?> GetForCheckoutPollingAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.OperationReceipts)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
    }
    public async Task<Guid?> GetRenterIdByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => (Guid?)o.RenterId)
            .FirstOrDefaultAsync(cancellationToken);
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
        RentalOrderStatus? status,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        IQueryable<RentalOrder> query = _dbContext.RentalOrders
            .AsNoTracking()
            .Where(o => o.RenterId == renterId);
        if (status.HasValue)
        {
            query = query.Where(o => o.Status == status.Value);
        }

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
        // Chỉ lấy receipt RequestRefund để biết dòng nào đang được hoàn; các receipt khác mang JSON lớn và không cần ở đây.
        return await _dbContext.RentalOrders
            .AsNoTracking()
            .AsSplitQuery()
            .Include(o => o.SubOrders)
                .ThenInclude(s => s.Items)
                    .ThenInclude(i => i.UnitAssignments)
            .Include(o => o.SubOrders)
                .ThenInclude(s => s.Items)
                    .ThenInclude(i => i.HandoverRecord)
            .Include(o => o.SubOrders)
                .ThenInclude(s => s.Items)
                    .ThenInclude(i => i.ReturnRecords)
            .Include(o => o.SubOrders)
                .ThenInclude(s => s.Items)
                    .ThenInclude(i => i.DeductionProposal)
            .Include(o => o.OperationReceipts.Where(r => r.Action == RequestRefundAction))
            .FirstOrDefaultAsync(o => o.Id == orderId && o.RenterId == renterId, cancellationToken);
    }

    public async Task<RecordPage<SupplierSubOrderSummaryRecord>> GetPaidSubOrderPageForSupplierAsync(
        Guid supplierId,
        RentalItemStatus? itemStatus,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        IQueryable<RentalSubOrder> query = PaidSubOrdersOf(supplierId);
        if (itemStatus.HasValue)
        {
            query = query.Where(s => s.Items.Any(i => i.Status == itemStatus.Value));
        }

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
            .AsSplitQuery()
            .Include(s => s.RentalOrder)
            .Include(s => s.Items)
                .ThenInclude(i => i.UnitAssignments)
            .Include(s => s.Items)
                .ThenInclude(i => i.HandoverRecord)
            .Include(s => s.Items)
                .ThenInclude(i => i.ReturnRecords)
            .Include(s => s.Items)
                .ThenInclude(i => i.DeductionProposal)
            .FirstOrDefaultAsync(s => s.Id == subOrderId, cancellationToken);
    }

    public async Task<RecordPage<AdminOrderSummaryRecord>> SearchOrdersForAdminAsync(
        AdminOrderSearchCriteria criteria,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        IQueryable<RentalOrder> query = _dbContext.RentalOrders.AsNoTracking();
        if (criteria.OrderCode is { } orderCode)
        {
            query = query.Where(o => o.OrderCode == orderCode);
        }

        if (criteria.SubOrderCode is { } subOrderCode)
        {
            query = query.Where(o => o.SubOrders.Any(s => s.SubOrderCode == subOrderCode));
        }

        if (criteria.RenterId is { } renterId)
        {
            query = query.Where(o => o.RenterId == renterId);
        }

        if (criteria.Status is { } status)
        {
            query = query.Where(o => o.Status == status);
        }

        if (criteria.CreatedFrom is { } from)
        {
            query = query.Where(o => o.CreatedAt >= from);
        }

        if (criteria.CreatedTo is { } to)
        {
            query = query.Where(o => o.CreatedAt < to);
        }

        // Các điều kiện cấp dòng cùng nằm trong một Any để chỉ khớp khi chính một dòng thỏa tất cả.
        if (criteria.RentalItemId.HasValue || criteria.ListingId.HasValue || criteria.AssetId.HasValue ||
            criteria.SupplierId.HasValue || criteria.ItemStatus.HasValue)
        {
            Guid? itemId = criteria.RentalItemId;
            Guid? listingId = criteria.ListingId;
            Guid? assetId = criteria.AssetId;
            Guid? supplierId = criteria.SupplierId;
            RentalItemStatus? itemStatus = criteria.ItemStatus;
            query = query.Where(o => o.Items.Any(i =>
                (itemId == null || i.Id == itemId) &&
                (listingId == null || i.EquipmentListingId == listingId) &&
                (supplierId == null || i.SupplierId == supplierId) &&
                (itemStatus == null || i.Status == itemStatus) &&
                (assetId == null || i.UnitAssignments.Any(u => u.AssetId == assetId))));
        }

        int totalCount = await query.CountAsync(cancellationToken);
        List<AdminOrderSummaryRecord> items = await query
            .OrderByDescending(o => o.CreatedAt)
            .ThenBy(o => o.Id)
            .Skip(skip)
            .Take(take)
            .Select(o => new AdminOrderSummaryRecord(
                o.Id,
                o.OrderCode,
                o.Status,
                o.RenterId,
                o.SubOrders.OrderBy(s => s.SubOrderCode).Select(s => s.SupplierId).ToList(),
                o.Currency,
                o.Items.Sum(i => i.RentalAmount),
                o.Items.Sum(i => i.DepositAmount),
                o.Items.Count,
                o.InvoiceRef,
                o.PaymentConfirmedAt,
                o.CreatedAt))
            .ToListAsync(cancellationToken);

        return new RecordPage<AdminOrderSummaryRecord>(items, totalCount);
    }

    public async Task<RentalOrder?> GetOrderForAdminAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .AsNoTracking()
            .AsSplitQuery()
            .Include(o => o.SubOrders)
                .ThenInclude(s => s.Items)
                    .ThenInclude(i => i.UnitAssignments)
            .Include(o => o.SubOrders)
                .ThenInclude(s => s.Items)
                    .ThenInclude(i => i.HandoverRecord)
            .Include(o => o.SubOrders)
                .ThenInclude(s => s.Items)
                    .ThenInclude(i => i.ReturnRecords)
            .Include(o => o.SubOrders)
                .ThenInclude(s => s.Items)
                    .ThenInclude(i => i.DeductionProposal)
            .Include(o => o.StateTransitions)
            .Include(o => o.OperationReceipts.Where(r => r.Action == RequestRefundAction))
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
    }

    // Nhà cung cấp chỉ thấy đơn sau khi người thuê đã thanh toán đúng hạn; thanh toán trễ sẽ được hoàn tiền.
    private IQueryable<RentalSubOrder> PaidSubOrdersOf(Guid supplierId) =>
        _dbContext.RentalSubOrders
            .AsNoTracking()
            .Where(s => s.SupplierId == supplierId &&
                        s.RentalOrder!.PaymentConfirmedAt != null &&
                        !s.RentalOrder.LatePaymentRefundRequired);

    public async Task<IReadOnlyList<Guid>> GetOverdueUnprotectedOrderIdsAsync(
    DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default)
    {
        DateOnly todayVn = RentalItem.TodayInVietnam(utcNow);

        return await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Status == RentalItemStatus.InRental
                     && i.ExpectedEndDate < todayVn
                     && i.UnitAssignments.Any(u =>
                            u.Status == RentalUnitAssignmentStatus.HandedOver
                            // Chỉ receipt "exhausted" mới không chặn; Succeeded/đang chạy/Business đều chặn.
                            && !i.RentalOrder!.OperationReceipts.Any(r =>
                                   r.Action == RentalItem.OverdueProtectionAction
                                && EF.Functions.Like(r.OperationId, "overdue:" + u.Id.ToString() + ":%")
                                && !(r.Status == RentalOperationReceipt.StatusFailed
                                     && (r.ErrorClassification == RentalOperationReceipt.ClassificationTransientExhausted
                                      || r.ErrorClassification == RentalOperationReceipt.ClassificationUncertainExhausted)))))
            .GroupBy(i => i.RentalOrderId)
            .OrderBy(g => g.Min(i => i.ExpectedEndDate))
            .Select(g => g.Key)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetHandoverOverdueOrderIdsAsync(
        DateOnly todayInVietnam, DateOnly autoExpireStartBefore, int batchSize,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Status == RentalItemStatus.AwaitingHandover
                     && i.ExpectedStartDate < todayInVietnam
                     && (i.ExpectedStartDate < autoExpireStartBefore ||
                         !i.RentalOrder!.OperationReceipts.Any(r =>
                             r.Action == "HandoverOverdueNotice" && r.TargetItemId == i.Id)))
            .GroupBy(i => i.RentalOrderId)
            .OrderBy(g => g.Min(i => i.ExpectedStartDate))
            .Select(g => g.Key)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetDeductionTimeoutItemIdsAsync(
    DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Status == RentalItemStatus.PendingSettlement
                     && i.DeductionDeadline != null
                     && i.DeductionDeadline < utcNow
                     && i.FinalDeductionAmount == null
                     && i.DeductionProposal == null)
            .OrderBy(i => i.DeductionDeadline)
            .Select(i => i.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<Guid>> GetRenterResponseTimeoutItemIdsAsync(
    DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Status == RentalItemStatus.PendingSettlement
                     && i.FinalDeductionAmount == null
                     && i.DeductionProposal != null
                     && i.DeductionProposal.Status == DeductionProposalStatus.Submitted
                     && i.DeductionProposal.RenterResponseDeadline < utcNow)
            .OrderBy(i => i.DeductionProposal!.RenterResponseDeadline)
            .Select(i => i.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetDisputedItemIdsAsync(
        int skip, int take, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Status == RentalItemStatus.PendingSettlement
                     && i.FinalDeductionAmount == null
                     && i.DeductionProposal != null
                     && i.DeductionProposal.Status == DeductionProposalStatus.Disputed)
            .OrderBy(i => i.DeductionProposal!.RespondedAt)     // khiếu nại cũ nhất lên đầu
            .Select(i => i.Id)
            .Skip(skip).Take(take)
            .ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<Guid>> GetSettlementReadyItemIdsAsync(
    int batchSize, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Status == RentalItemStatus.PendingSettlement
                     && i.FinalDeductionAmount != null
                     && i.InventoryClosureConfirmedAt != null
                     && i.SettlementRef == null
                     && !i.RentalOrder!.OperationReceipts.Any(r =>
                            r.Action == RentalItem.SettleRentalAction && r.TargetItemId == i.Id))
            .OrderBy(i => i.FinalDeductionAmount)   // thứ tự ổn định; thay bằng mốc chốt nếu có
            .Select(i => i.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }
}
