using Dprental.AnalyticsService.Domain.Entities;

namespace Dprental.AnalyticsService.Domain.IRepositories;

/// <summary>
/// Đọc/ghi analytics_rental_orders và analytics_rental_items cho projector và dashboard queries.
/// </summary>
public interface IRentalFactRepository
{
    Task<RentalOrderFact?> FindOrderAsync(Guid rentalOrderId, CancellationToken cancellationToken = default);

    Task<RentalItemFact?> FindItemAsync(Guid rentalItemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RentalItemFact>> FindItemsByOrderAsync(Guid rentalOrderId, CancellationToken cancellationToken = default);

    void AddOrder(RentalOrderFact order);

    void AddItem(RentalItemFact item);

    Task<OrderFactCounts> GetOrderMetricsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? supplierId = null,
        CancellationToken cancellationToken = default);

    Task<int> GetActiveOrdersCountAsync(
        DateTimeOffset endUtc,
        Guid? supplierId = null,
        CancellationToken cancellationToken = default);

    Task<RentalFactMetrics> GetRentalMetricsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? supplierId = null,
        CancellationToken cancellationToken = default);

    Task<EquipmentRankingsResult> GetEquipmentRankingsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, int>> GetSupplierRequestCountsAsync(
        IReadOnlyCollection<Guid> supplierIds,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, SupplierRentalStatRow>> GetSupplierRentalStatsAsync(
        IReadOnlyCollection<Guid> supplierIds,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<DateOnly, int[]>> GetOrderTrendPointsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> FindSupplierIdsByOrdersOrItemsAsync(
        IReadOnlyCollection<Guid> orderIds,
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken = default);
}
