using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public sealed partial class RentalOrderRepository
{
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
        // Đếm theo sự kiện lịch sử, không theo Status hiện tại: item đã hoàn tất
        // vẫn phải được tính là từng được supplier chấp nhận. Lọc supplier tại DB.
        var history = _dbContext.RentalStateTransitions.AsNoTracking()
            .Where(t => t.RentalItem != null && t.RentalItem.SupplierId == supplierId);
        // Chỉ chấp nhận thao tác của đúng supplier sở hữu item; không tính admin/renter.
        // Query này còn dùng làm điều kiện tồn tại cho hủy sau chấp nhận.
        var accepted = history.Where(t =>
            t.Action == "SupplierAccept" &&
            t.FromState == "PendingSupplierConfirmation" && t.ToState == "AwaitingHandover" &&
            t.ActorRole == ActorRole.Supplier && t.ActorId == supplierId);

        // Retry có thể để lại nhiều sự kiện tương đương: mỗi item/action chỉ lấy lần đầu.
        // Timeout do System tạo, nên không áp điều kiện ActorId của supplier cho nó.
        var decisions = history.Where(t =>
            t.FromState == "PendingSupplierConfirmation" &&
            ((t.Action == "SupplierAccept" && t.ToState == "AwaitingHandover" &&
              t.ActorRole == ActorRole.Supplier && t.ActorId == supplierId) ||
             (t.Action == "SupplierReject" && t.ToState == "Rejected" &&
              t.ActorRole == ActorRole.Supplier && t.ActorId == supplierId) ||
             (t.Action == "SupplierTimeout" && t.ToState == "Expired" &&
              t.ActorRole == ActorRole.System)))
            .GroupBy(t => new { t.RentalItemId, t.Action })
            .Select(g => new { Kind = g.Key.Action, FirstOccurredAt = g.Min(t => t.OccurredAt) });

        // Chỉ tính hủy bởi supplier sau một lần accept hợp lệ (kể cả accept ở tháng trước).
        // EXISTS được dịch từ Any; không tải lịch sử về RAM để tìm kiếm.
        var cancellations = history.Where(t =>
            t.ToState == "Cancelled" && t.FromState != "Cancelled" &&
            t.ActorRole == ActorRole.Supplier && t.ActorId == supplierId &&
            accepted.Any(a => a.RentalItemId == t.RentalItemId && a.OccurredAt <= t.OccurredAt))
            .GroupBy(t => t.RentalItemId)
            .Select(g => new { Kind = "CancelAfterAccept", FirstOccurredAt = g.Min(t => t.OccurredAt) });

        // Phải lấy MIN trên toàn lịch sử rồi mới lọc [fromUtc, toUtc).
        // Nếu lọc trước, bản retry trong tháng sẽ bị đếm dù lần đầu nằm ở tháng trước.
        // Concat + GroupBy tổng hợp bốn chỉ số trong một lệnh DB; không có event trả về 0.
        var result = await decisions.Concat(cancellations)
            .Where(t => t.FirstOccurredAt >= fromUtc && t.FirstOccurredAt < toUtc)
            .GroupBy(t => 1)
            .Select(g => new SupplierConfirmationStatisticsRecord(
                g.LongCount(t => t.Kind == "SupplierAccept"),
                g.LongCount(t => t.Kind == "SupplierReject"),
                g.LongCount(t => t.Kind == "SupplierTimeout"),
                g.LongCount(t => t.Kind == "CancelAfterAccept")))
            .FirstOrDefaultAsync(cancellationToken);

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
        bool? refunded,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        IQueryable<RentalOrder> orders = _dbContext.RentalOrders
            .AsNoTracking()
            .Where(o => o.RenterId == renterId);
        if (status.HasValue)
        {
            orders = orders.Where(o => o.Status == status.Value);
        }

        // Khoản hoàn: yêu cầu RequestRefund của một dòng, hoặc SettleRental khi dòng còn cọc để hoàn sau khấu trừ;
        // khoản chờ hoàn là khoản chưa có xác nhận chuyển khoản.
        var query = orders.Select(o => new
        {
            Order = o,
            RefundCount = o.OperationReceipts.Count(r => r.TargetItemId != null
                && (r.Action == RequestRefundAction
                    || (r.Action == RentalItem.SettleRentalAction
                        && o.Items.Any(i => i.Id == r.TargetItemId && i.DepositAmount - (i.FinalDeductionAmount ?? 0m) > 0m)))),
            PendingRefundCount = o.OperationReceipts.Count(r => r.TargetItemId != null
                && (r.Action == RequestRefundAction
                    || (r.Action == RentalItem.SettleRentalAction
                        && o.Items.Any(i => i.Id == r.TargetItemId && i.DepositAmount - (i.FinalDeductionAmount ?? 0m) > 0m)))
                && !o.RefundConfirmations.Any(c => c.RentalItemId == r.TargetItemId))
        });
        if (refunded == true)
        {
            query = query.Where(x => x.RefundCount > 0 && x.PendingRefundCount == 0);
        }
        else if (refunded == false)
        {
            query = query.Where(x => x.PendingRefundCount > 0);
        }

        int totalCount = await query.CountAsync(cancellationToken);
        List<RenterOrderSummaryRecord> items = await query
            .OrderByDescending(x => x.Order.CreatedAt)
            .ThenBy(x => x.Order.Id)
            .Skip(skip)
            .Take(take)
            .Select(x => new RenterOrderSummaryRecord(
                x.Order.Id,
                x.Order.OrderCode,
                x.Order.Status,
                x.Order.Currency,
                x.Order.Items.Sum(i => i.RentalAmount),
                x.Order.Items.Sum(i => i.DepositAmount),
                x.Order.Items.Count,
                x.Order.SubOrders.Count,
                x.Order.PaymentDeadline,
                x.Order.CreatedAt,
                x.Order.Items
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
                    .ToList(),
                x.Order.Version,
                x.RefundCount,
                x.PendingRefundCount,
                x.Order.RefundConfirmations.Sum(c => c.RefundAmount)))
            .ToListAsync(cancellationToken);

        return new RecordPage<RenterOrderSummaryRecord>(items, totalCount);
    }

    public async Task<RentalOrder?> GetRenterOrderWithSubOrdersAsync(
        Guid orderId,
        Guid renterId,
        CancellationToken cancellationToken = default)
    {
        // Chỉ lấy receipt yêu cầu hoàn (RequestRefund, SettleRental) để biết dòng nào cần hoàn;
        // các receipt khác mang JSON lớn và không cần ở đây.
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
                    .ThenInclude(i => i.ReturnRequests)
                        .ThenInclude(r => r.Units)
            .Include(o => o.SubOrders)
                .ThenInclude(s => s.Items)
                    .ThenInclude(i => i.ReturnRequests)
                        .ThenInclude(r => r.Issues)
                            .ThenInclude(issue => issue.Units)
            .Include(o => o.SubOrders)
                .ThenInclude(s => s.Items)
                    .ThenInclude(i => i.ReturnRequests)
                        .ThenInclude(r => r.Resolutions)
                            .ThenInclude(res => res.Units)
            .Include(o => o.SubOrders)
                .ThenInclude(s => s.Items)
                    .ThenInclude(i => i.DeductionProposal)
            .Include(o => o.OperationReceipts.Where(r => r.Action == RequestRefundAction || r.Action == RentalItem.SettleRentalAction))
            .Include(o => o.RefundConfirmations)
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
                s.CreatedAt,
                s.RentalOrder.Version))
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
                .ThenInclude(i => i.ReturnRequests)
                    .ThenInclude(r => r.Units)
            .Include(s => s.Items)
                .ThenInclude(i => i.ReturnRequests)
                    .ThenInclude(r => r.Issues)
                        .ThenInclude(issue => issue.Units)
            .Include(s => s.Items)
                .ThenInclude(i => i.ReturnRequests)
                    .ThenInclude(r => r.Resolutions)
                        .ThenInclude(res => res.Units)
            .Include(s => s.Items)
                .ThenInclude(i => i.DeductionProposal)
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
