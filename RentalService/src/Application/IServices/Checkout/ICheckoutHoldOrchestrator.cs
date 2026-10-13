using Dprental.RentalService.Application.DTOs.Checkout;

namespace Dprental.RentalService.Application.IServices.Checkout;

/// <summary>
/// Giữ lịch Inventory cho mọi dòng của một lần checkout theo kiểu all-or-nothing:
/// một dòng không giữ được thì nhả các dòng đã giữ và không có đơn hàng nào được tạo.
/// </summary>
public interface ICheckoutHoldOrchestrator
{
    /// <summary>Giữ đúng số lượng của từng dòng trong kế hoạch, hạn giữ bằng hạn thanh toán.</summary>
    Task<CheckoutHoldResult> HoldAllLinesAsync(
        CheckoutPlan plan,
        string checkoutOperationId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>Nhả các dòng đã giữ. Không nhận CancellationToken vì việc nhả không được bỏ dở.</summary>
    Task ReleaseAllLinesAsync(IReadOnlyList<CheckoutHeldLine> heldLines);
}
