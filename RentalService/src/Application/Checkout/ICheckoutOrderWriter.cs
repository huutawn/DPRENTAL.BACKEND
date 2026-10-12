using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Checkout;

/// <summary>
/// Lưu RentalOrder, RentalItem, RentalUnitAssignment, RentalTermsAcceptance và cập nhật giỏ trong một transaction
/// sau khi mọi dòng đã giữ lịch. Lưu thất bại thì nhả các hold đã giữ.
/// </summary>
public interface ICheckoutOrderWriter
{
    /// <param name="utcNow">Phải là cùng thời điểm đã dùng khi giữ lịch để hạn thanh toán trùng hạn giữ.</param>
    Task<Result<RentalOrder>> CreateOrderAsync(
        CheckoutPlan plan,
        IReadOnlyList<CheckoutHeldLine> heldLines,
        string checkoutOperationId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);
}
