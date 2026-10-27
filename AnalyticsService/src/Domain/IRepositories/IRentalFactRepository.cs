using Dprental.AnalyticsService.Domain.Entities;

namespace Dprental.AnalyticsService.Domain.IRepositories;

/// <summary>
/// Đọc/ghi analytics_rental_orders và analytics_rental_items cho projector. Hàm Find trả entity được
/// theo dõi vì projector sẽ sửa; việc lưu do dispatcher quyết định qua <see cref="IAnalyticsUnitOfWork"/>.
/// </summary>
public interface IRentalFactRepository
{
    Task<RentalOrderFact?> FindOrderAsync(Guid rentalOrderId, CancellationToken cancellationToken = default);

    Task<RentalItemFact?> FindItemAsync(Guid rentalItemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RentalItemFact>> FindItemsByOrderAsync(Guid rentalOrderId, CancellationToken cancellationToken = default);

    void AddOrder(RentalOrderFact order);

    void AddItem(RentalItemFact item);
}
