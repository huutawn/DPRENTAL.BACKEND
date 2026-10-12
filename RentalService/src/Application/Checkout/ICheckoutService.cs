namespace Dprental.RentalService.Application.Checkout;

/// <summary>
/// Gửi đơn hàng từ giỏ: chuẩn bị, giữ lịch, lưu đơn rồi tạo hóa đơn. Gửi lại với cùng Idempotency-Key
/// trả về đúng đơn đã tạo, không tạo thêm đơn và không giữ thêm lịch.
/// </summary>
public interface ICheckoutService
{
    Task<CheckoutResult> SubmitAsync(
        CheckoutRenterContext renter,
        CheckoutRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}
