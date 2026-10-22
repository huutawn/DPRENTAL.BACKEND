using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Checkout;

public interface ICheckoutService
{
    Task<CheckoutSubmitResult> SubmitAsync(
        CheckoutRenterContext renter,
        CheckoutRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<CheckoutSubmitResult> ContinueOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    Task<Result<CheckoutOperationStatusResponse>> GetOperationStatusAsync(
        string operationId,
        Guid renterUserId,
        CancellationToken cancellationToken = default);
}
