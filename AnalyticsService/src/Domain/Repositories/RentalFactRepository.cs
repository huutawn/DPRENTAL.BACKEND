using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Domain.Repositories;

/// <summary>
/// Cài đặt <see cref="IRentalFactRepository"/> bằng EF Core. Không gọi SaveChanges.
/// </summary>
public sealed class RentalFactRepository : IRentalFactRepository
{
    private readonly AnalyticsServiceDbContext _context;

    public RentalFactRepository(AnalyticsServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    // FindAsync tìm trong bộ nhớ (dòng vừa Add, chưa lưu) trước, rồi mới hỏi DB
    public Task<RentalOrderFact?> FindOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        _context.RentalOrderFacts.FindAsync([orderId], cancellationToken).AsTask();

    public Task<RentalItemFact?> FindItemAsync(Guid rentalItemId, CancellationToken cancellationToken = default) =>
        _context.RentalItemFacts.FindAsync([rentalItemId], cancellationToken).AsTask();

    // Có tracking (không AsNoTracking) vì projector sửa các item này; lọc trên DB qua index OrderId
    public async Task<IReadOnlyList<RentalItemFact>> FindItemsByOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        await _context.RentalItemFacts
            .Where(item => item.OrderId == orderId)
            .ToListAsync(cancellationToken);

    public void AddOrder(RentalOrderFact order) => _context.RentalOrderFacts.Add(order);

    public void AddItem(RentalItemFact item) => _context.RentalItemFacts.Add(item);
}
