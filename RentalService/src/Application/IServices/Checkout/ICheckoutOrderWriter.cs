using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.IServices.Checkout;

public interface ICheckoutOrderWriter
{
    Task<Result<RentalOrder>> CreateOrderAsync(
        CheckoutPlan plan,
        IReadOnlyList<CheckoutHeldLine> heldLines,
        string checkoutOperationId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    Task<Result<RentalOrder>> CreateOrderAsync(
        CheckoutPlan plan,
        IReadOnlyList<CheckoutHeldLine> heldLines,
        string checkoutOperationId,
        Guid expectedRentalOrderId,
        DateTimeOffset holdExpiresAt,
        DateTimeOffset orderCreatedAt,
        CancellationToken cancellationToken = default);
}
