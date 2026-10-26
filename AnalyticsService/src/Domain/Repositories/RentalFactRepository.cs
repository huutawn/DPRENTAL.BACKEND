using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Domain.Repositories;

public sealed class RentalFactRepository : IRentalFactRepository
{
    private readonly AnalyticsServiceDbContext _context;

    public RentalFactRepository(AnalyticsServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    // FindAsync tìm trong bộ nhớ (dòng vừa Add, chưa lưu) trước, rồi mới hỏi DB
    public Task<RentalOrderFact?> FindOrderAsync(Guid rentalOrderId, CancellationToken cancellationToken = default) =>
        _context.RentalOrders.FindAsync([rentalOrderId], cancellationToken).AsTask();

    public Task<RentalItemFact?> FindItemAsync(Guid rentalItemId, CancellationToken cancellationToken = default) =>
        _context.RentalItems.FindAsync([rentalItemId], cancellationToken).AsTask();

    // Có tracking vì projector sửa các item này; lọc trên DB qua index RentalOrderId
    public async Task<IReadOnlyList<RentalItemFact>> FindItemsByOrderAsync(Guid rentalOrderId, CancellationToken cancellationToken = default) =>
        await _context.RentalItems.Where(item => item.RentalOrderId == rentalOrderId).ToListAsync(cancellationToken);

    public void AddOrder(RentalOrderFact order) => _context.RentalOrders.Add(order);

    public void AddItem(RentalItemFact item) => _context.RentalItems.Add(item);
}
