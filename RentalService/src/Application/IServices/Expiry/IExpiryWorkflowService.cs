using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Expiry;

public interface IExpiryWorkflowService
{
    Task<int> ProcessPaymentTimeoutsAsync(CancellationToken cancellationToken = default);

    Task<int> ProcessSupplierTimeoutsAsync(CancellationToken cancellationToken = default);

    Task<Result> ExpireSingleOrderPaymentAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<Result> ExpireSingleItemSupplierTimeoutAsync(Guid itemId, CancellationToken cancellationToken = default);
}
