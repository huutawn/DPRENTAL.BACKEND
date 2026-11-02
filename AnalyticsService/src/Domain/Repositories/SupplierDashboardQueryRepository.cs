using Dprental.AnalyticsService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Domain.Repositories;

public sealed class SupplierDashboardQueryRepository(AnalyticsServiceDbContext context) : ISupplierDashboardQueryRepository
{
    public async Task<SupplierCandidatePage> GetCandidatesAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, int offset, int pageSize,
        CancellationToken cancellationToken = default)
    {
        var rental = from item in context.RentalItems.AsNoTracking()
                     join order in context.RentalOrders.AsNoTracking()
                         on item.RentalOrderId equals order.RentalOrderId
                     where (order.CreatedAt >= startUtc && order.CreatedAt < endUtc) ||
                           (order.PaidAt >= startUtc && order.PaidAt < endUtc) ||
                           (item.ConfirmedAt >= startUtc && item.ConfirmedAt < endUtc) ||
                           (item.RejectedAt >= startUtc && item.RejectedAt < endUtc) ||
                           (item.ExpiredAt >= startUtc && item.ExpiredAt < endUtc) ||
                           (item.CompletedAt >= startUtc && item.CompletedAt < endUtc)
                     select item.SupplierId;

        var payment = context.PaymentFinancialEvents.AsNoTracking()
            .Where(x => x.SupplierId != null && x.CompletedAt >= startUtc && x.CompletedAt < endUtc)
            .Select(x => x.SupplierId!.Value);

        var candidates = rental.Union(payment);
        int total = await candidates.CountAsync(cancellationToken);
        var ids = await candidates.OrderBy(x => x).Skip(offset).Take(pageSize).ToListAsync(cancellationToken);
        return new SupplierCandidatePage(total, ids);
    }
}
