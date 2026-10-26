using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;

namespace Dprental.AnalyticsService.Domain.Repositories;

/// <summary>
/// Cài đặt <see cref="IFinancialFactRepository"/> bằng EF Core. Không gọi SaveChanges.
/// </summary>
public sealed class FinancialFactRepository : IFinancialFactRepository
{
    private readonly AnalyticsServiceDbContext _context;

    public FinancialFactRepository(AnalyticsServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public Task<FinancialSettlementFact?> FindSettlementAsync(string settlementRef, CancellationToken cancellationToken = default) =>
        _context.FinancialSettlementFacts.FindAsync([settlementRef], cancellationToken).AsTask();

    // Khóa chính 2 cột: truyền đủ 2 giá trị theo đúng thứ tự khai báo HasKey(RefundRef, RentalItemId)
    public Task<RefundFact?> FindRefundAsync(string refundRef, Guid rentalItemId, CancellationToken cancellationToken = default) =>
        _context.RefundFacts.FindAsync([refundRef, rentalItemId], cancellationToken).AsTask();

    public void AddSettlement(FinancialSettlementFact settlement) => _context.FinancialSettlementFacts.Add(settlement);

    public void AddRefund(RefundFact refund) => _context.RefundFacts.Add(refund);
}
