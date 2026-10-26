using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Application.Dashboard;

public sealed class DashboardQueryService(AnalyticsServiceDbContext db)
{
    public async Task<OverviewResponse> OverviewAsync(DashboardDateRange range, CancellationToken ct)
    {
        var orders = db.RentalOrders.AsNoTracking();
        var items = db.RentalItems.AsNoTracking();
        var financial = db.PaymentFinancialEvents.AsNoTracking();

        var orderMetrics = new OrderMetrics(
            await orders.CountAsync(x => x.CreatedAt >= range.StartUtc && x.CreatedAt < range.EndUtc, ct),
            await orders.CountAsync(x => x.PaidAt >= range.StartUtc && x.PaidAt < range.EndUtc, ct),
            await ActiveCountAsync(ct),
            await orders.CountAsync(x => x.CompletedAt >= range.StartUtc && x.CompletedAt < range.EndUtc, ct),
            await orders.CountAsync(x => x.CancelledAt >= range.StartUtc && x.CancelledAt < range.EndUtc, ct),
            await orders.CountAsync(x => x.ExpiredAt >= range.StartUtc && x.ExpiredAt < range.EndUtc, ct));

        var paidItems = from item in items
                        join order in orders on item.RentalOrderId equals order.RentalOrderId
                        where order.PaidAt >= range.StartUtc && order.PaidAt < range.EndUtc
                        select item;
        var settled = financial.Where(x => x.MessageType == PaymentAnalyticsEventTypes.RentalItemFinancialSettled &&
            x.CompletedAt >= range.StartUtc && x.CompletedAt < range.EndUtc);
        var refunds = financial.Where(x => x.MessageType == PaymentAnalyticsEventTypes.RefundCompletedFinancial &&
            x.CompletedAt >= range.StartUtc && x.CompletedAt < range.EndUtc);
        var payouts = financial.Where(x => x.MessageType == PaymentAnalyticsEventTypes.SupplierPayoutCompleted &&
            x.CompletedAt >= range.StartUtc && x.CompletedAt < range.EndUtc);

        var money = new FinancialMetrics(
            await paidItems.SumAsync(x => (decimal?)x.RentalAmount, ct) ?? 0,
            await items.Where(x => x.CompletedAt >= range.StartUtc && x.CompletedAt < range.EndUtc)
                .SumAsync(x => (decimal?)x.RentalAmount, ct) ?? 0,
            await paidItems.SumAsync(x => (decimal?)x.DepositAmount, ct) ?? 0,
            await settled.SumAsync(x => x.PlatformFeeAmount, ct) ?? 0,
            await settled.SumAsync(x => x.FinalDeductionAmount, ct) ?? 0,
            await refunds.SumAsync(x => x.Amount, ct) ?? 0,
            await settled.SumAsync(x => x.SupplierPayable, ct) ?? 0,
            await payouts.SumAsync(x => x.Amount, ct) ?? 0);

        var rental = new RentalMetrics(
            await paidItems.CountAsync(ct),
            await items.Where(x => x.HandoverAt >= range.StartUtc && x.HandoverAt < range.EndUtc)
                .SumAsync(x => (int?)x.Quantity, ct) ?? 0,
            await items.CountAsync(x => x.CompletedAt >= range.StartUtc && x.CompletedAt < range.EndUtc, ct));

        return new OverviewResponse(range.From, range.To, "VND", orderMetrics, money, rental);
    }

    public Task<int> ActiveCountAsync(CancellationToken ct) => db.RentalOrders.AsNoTracking()
        .CountAsync(x => x.PaidAt != null && x.Status != "COMPLETED" &&
            x.Status != "CANCELLED" && x.Status != "EXPIRED" && x.Status != "REJECTED", ct);

    public async Task<EquipmentResponse> EquipmentAsync(DashboardDateRange range, int limit, CancellationToken ct)
    {
        var paid = from item in db.RentalItems.AsNoTracking()
                   join order in db.RentalOrders.AsNoTracking() on item.RentalOrderId equals order.RentalOrderId
                   where order.PaidAt >= range.StartUtc && order.PaidAt < range.EndUtc
                   select item;

        var types = await paid.GroupBy(x => x.EquipmentTypeId)
            .Select(g => new EquipmentRank(g.Key, g.Max(x => x.EquipmentTypeName) ?? "Không xác định",
                g.Count(), g.Sum(x => x.HandoverAt != null ? x.Quantity : 0), g.Sum(x => x.RentalAmount)))
            .OrderByDescending(x => x.RentalItemCount).ThenByDescending(x => x.RentedQuantity)
            .ThenBy(x => x.Id).Take(limit).ToListAsync(ct);
        var listings = await paid.GroupBy(x => x.EquipmentListingId)
            .Select(g => new EquipmentRank(g.Key, string.Empty, g.Count(),
                g.Sum(x => x.HandoverAt != null ? x.Quantity : 0), g.Sum(x => x.RentalAmount)))
            .OrderByDescending(x => x.RentalItemCount).ThenByDescending(x => x.RentedQuantity)
            .ThenBy(x => x.Id).Take(limit).ToListAsync(ct);
        listings = listings.Select(x => x with { Name = x.Id?.ToString("D") ?? "Không xác định" }).ToList();
        var brands = await paid.GroupBy(x => x.BrandId)
            .Select(g => new EquipmentRank(g.Key, g.Max(x => x.BrandName) ?? "Không xác định",
                g.Count(), g.Sum(x => x.HandoverAt != null ? x.Quantity : 0), g.Sum(x => x.RentalAmount)))
            .OrderByDescending(x => x.RentalItemCount).ThenByDescending(x => x.RentedQuantity)
            .ThenBy(x => x.Id).Take(limit).ToListAsync(ct);
        return new EquipmentResponse(range.From, range.To, types, listings, brands);
    }

    public async Task<SuppliersResponse> SuppliersAsync(
        DashboardDateRange range, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Pagination must use page >= 1 and pageSize 1..100.");
        }

        var rentalCandidates = from item in db.RentalItems.AsNoTracking()
                               join order in db.RentalOrders.AsNoTracking()
                                   on item.RentalOrderId equals order.RentalOrderId
                               where order.PaidAt >= range.StartUtc && order.PaidAt < range.EndUtc ||
                                   item.ConfirmedAt >= range.StartUtc && item.ConfirmedAt < range.EndUtc ||
                                   item.RejectedAt >= range.StartUtc && item.RejectedAt < range.EndUtc ||
                                   item.ExpiredAt >= range.StartUtc && item.ExpiredAt < range.EndUtc ||
                                   item.CompletedAt >= range.StartUtc && item.CompletedAt < range.EndUtc
                               select item.SupplierId;
        var paymentCandidates = db.PaymentFinancialEvents.AsNoTracking()
            .Where(x => x.SupplierId != null && x.CompletedAt >= range.StartUtc && x.CompletedAt < range.EndUtc)
            .Select(x => x.SupplierId!.Value);
        var candidates = rentalCandidates.Union(paymentCandidates);
        int total = await candidates.CountAsync(ct);
        Guid[] ids = await candidates.OrderBy(x => x).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        if (ids.Length == 0)
        {
            return new SuppliersResponse(range.From, range.To, page, pageSize, total, []);
        }

        var rentalStats = await (from item in db.RentalItems.AsNoTracking()
            join order in db.RentalOrders.AsNoTracking() on item.RentalOrderId equals order.RentalOrderId
            where ids.Contains(item.SupplierId)
            group new { item, order } by item.SupplierId into g
            select new
            {
                SupplierId = g.Key,
                Accepted = g.Sum(x => x.item.ConfirmedAt >= range.StartUtc && x.item.ConfirmedAt < range.EndUtc ? 1 : 0),
                Rejected = g.Sum(x => x.item.RejectedAt >= range.StartUtc && x.item.RejectedAt < range.EndUtc &&
                    x.item.RejectionReason == "SUPPLIER_REJECTED" ? 1 : 0),
                Timeout = g.Sum(x => x.item.ExpiredAt >= range.StartUtc && x.item.ExpiredAt < range.EndUtc &&
                    x.item.ExpiryReason == "SUPPLIER_CONFIRMATION_EXPIRED" ? 1 : 0),
                Completed = g.Sum(x => x.item.CompletedAt >= range.StartUtc && x.item.CompletedAt < range.EndUtc ? 1 : 0),
                RentalValue = g.Sum(x => x.order.PaidAt >= range.StartUtc && x.order.PaidAt < range.EndUtc
                    ? x.item.RentalAmount : 0)
            }).ToDictionaryAsync(x => x.SupplierId, ct);

        var financialStats = await db.PaymentFinancialEvents.AsNoTracking()
            .Where(x => x.SupplierId != null && ids.Contains(x.SupplierId.Value) &&
                x.CompletedAt >= range.StartUtc && x.CompletedAt < range.EndUtc)
            .GroupBy(x => x.SupplierId!.Value)
            .Select(g => new
            {
                SupplierId = g.Key,
                Payable = g.Sum(x => x.MessageType == PaymentAnalyticsEventTypes.RentalItemFinancialSettled
                    ? x.SupplierPayable : 0),
                Paid = g.Sum(x => x.MessageType == PaymentAnalyticsEventTypes.SupplierPayoutCompleted
                    ? x.Amount : 0)
            }).ToDictionaryAsync(x => x.SupplierId, ct);

        var result = ids.Select(id =>
        {
            rentalStats.TryGetValue(id, out var rental);
            financialStats.TryGetValue(id, out var money);
            return new SupplierMetrics(id, rental?.Accepted ?? 0, rental?.Rejected ?? 0,
                rental?.Timeout ?? 0, rental?.Completed ?? 0, rental?.RentalValue ?? 0,
                money?.Payable ?? 0, money?.Paid ?? 0);
        }).ToList();
        return new SuppliersResponse(range.From, range.To, page, pageSize, total, result);
    }
}
