using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Domain.Repositories;

public sealed class PaymentFinancialEventRepository : IPaymentFinancialEventRepository
{
    private readonly AnalyticsServiceDbContext _context;

    public PaymentFinancialEventRepository(AnalyticsServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public Task<bool> ExistsMessageAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        _context.PaymentFinancialEvents.AnyAsync(x => x.MessageId == messageId, cancellationToken);

    // Chỉ đọc để so sánh, không sửa: AsNoTracking. Dùng unique index LogicalEventId.
    public Task<PaymentFinancialEvent?> FindByLogicalEventIdAsync(string logicalEventId, CancellationToken cancellationToken = default) =>
        _context.PaymentFinancialEvents.AsNoTracking()
            .FirstOrDefaultAsync(x => x.LogicalEventId == logicalEventId, cancellationToken);

    public void Add(PaymentFinancialEvent financialEvent) => _context.PaymentFinancialEvents.Add(financialEvent);
}
