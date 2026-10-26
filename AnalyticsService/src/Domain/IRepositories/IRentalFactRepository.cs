using Dprental.AnalyticsService.Domain.Entities;

namespace Dprental.AnalyticsService.Domain.IRepositories;

/// <summary>
/// Truy cập rental_order_facts và rental_item_facts cho projector. Các hàm Find trả về entity được
/// theo dõi (tracked) vì projector sẽ sửa chúng; việc lưu do tầng điều phối (dispatcher) quyết định.
/// </summary>
public interface IRentalFactRepository
{
    Task<RentalOrderFact?> FindOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<RentalItemFact?> FindItemAsync(Guid rentalItemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RentalItemFact>> FindItemsByOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    void AddOrder(RentalOrderFact order);

    void AddItem(RentalItemFact item);
}
