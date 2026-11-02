using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public sealed partial class RentalOrderRepository
{
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
                o.CreatedAt,
                o.Items
                    .OrderBy(i => i.ExpectedStartDate)
                    .ThenBy(i => i.Id)
                    .Select(i => new OrderItemSummaryRecord(
                        i.Id,
                        i.EquipmentListingId,
                        i.SupplierId,
                        i.ListingTitleSnapshot,
                        i.ThumbnailMediaIdSnapshot,
                        i.Quantity,
                        i.DailyRateSnapshot,
                        i.RentalAmount,
                        i.DepositAmount,
                        i.ExpectedStartDate,
                        i.ExpectedEndDate,
                        i.Status))
                    .ToList()))
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
            .Include(o => o.OperationReceipts.Where(r => r.Action == RequestRefundAction || r.Action == RentalItem.SettleRentalAction))
            .Include(o => o.RefundConfirmations)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
    }

    public async Task<RecordPage<AdminReturnRequestRecord>> SearchReturnRequestsForAdminAsync(
        AdminReturnRequestSearchCriteria criteria,
        DateTimeOffset now,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var query = _dbContext.ReturnRequests.AsNoTracking();

        if (criteria.ItemId.HasValue)
        {
            query = query.Where(r => r.RentalItemId == criteria.ItemId.Value);
        }

        if (criteria.OrderId.HasValue)
        {
            query = query.Where(r => r.RentalItem!.RentalOrderId == criteria.OrderId.Value);
        }

        if (criteria.Disputed == true)
        {
            query = query.Where(r => r.Units.Any(u => u.ClosedAt == null && u.RejectedAt == null &&
                r.Issues.Any(i => i.Units.Any(iu => iu.RentalUnitAssignmentId == u.RentalUnitAssignmentId))));
        }
        else if (criteria.Disputed == false)
        {
            query = query.Where(r => !r.Units.Any(u => u.ClosedAt == null && u.RejectedAt == null &&
                r.Issues.Any(i => i.Units.Any(iu => iu.RentalUnitAssignmentId == u.RentalUnitAssignmentId))));
        }

        if (criteria.Overdue == true)
        {
            query = query.Where(r => r.ConfirmationDeadlineAt <= now &&
                r.Units.Any(u => u.ClosedAt == null && u.RejectedAt == null));
        }
        else if (criteria.Overdue == false)
        {
            query = query.Where(r => r.ConfirmationDeadlineAt > now ||
                !r.Units.Any(u => u.ClosedAt == null && u.RejectedAt == null));
        }

        int totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(r => r.SubmittedAt)
            .ThenBy(r => r.Id)
            .Skip(skip)
            .Take(take)
            .Select(r => new AdminReturnRequestRecord(
                r.Id,
                r.RentalItemId,
                r.RentalItem!.RentalOrderId,
                r.RentalItem.RentalOrder!.OrderCode,
                r.RentalItem.RentalOrder.RenterId,
                r.RentalItem.SupplierId,
                r.SubmittedAt,
                r.ConfirmationDeadlineAt,
                r.Units.Select(u => new AdminReturnRequestUnitRecord(
                    u.RentalUnitAssignmentId,
                    u.ClosedAt,
                    u.RejectedAt)).ToList(),
                r.Issues.SelectMany(i => i.Units.Select(iu => iu.RentalUnitAssignmentId)).ToList()))
            .ToListAsync(cancellationToken);

        return new RecordPage<AdminReturnRequestRecord>(items, totalCount);
    }
}
