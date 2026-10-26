using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;

namespace Dprental.AnalyticsService.Application.Projections;

/// <summary>
/// Tìm dòng đơn / item; chưa có thì tạo "dòng tạm" chỉ có khóa. Các cột khác chờ event tương ứng
/// điền vào, nên event đến sai thứ tự không bị mất.
/// </summary>
internal static class RentalFactRows
{
    public static async Task<RentalOrderFact> GetOrCreateOrderAsync(
        IRentalFactRepository repository, Guid rentalOrderId, CancellationToken cancellationToken)
    {
        RentalOrderFact? order = await repository.FindOrderAsync(rentalOrderId, cancellationToken);
        if (order is not null)
        {
            return order;
        }

        order = new RentalOrderFact { RentalOrderId = rentalOrderId };
        repository.AddOrder(order);
        return order;
    }

    public static async Task<RentalItemFact> GetOrCreateItemAsync(
        IRentalFactRepository repository, Guid rentalItemId, Guid rentalOrderId, CancellationToken cancellationToken)
    {
        RentalItemFact? item = await repository.FindItemAsync(rentalItemId, cancellationToken);
        if (item is not null)
        {
            return item;
        }

        item = new RentalItemFact { RentalItemId = rentalItemId, RentalOrderId = rentalOrderId };
        repository.AddItem(item);
        return item;
    }
}
