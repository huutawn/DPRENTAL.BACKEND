using Dprental.RentalService.Application.DTOs.Checkout;

namespace Dprental.RentalService.Application.IServices.Checkout;

/// <summary>
/// Bước đầu của checkout: validate renter, giỏ, Listing, giá, lịch, điều khoản, nơi giao và dựng snapshot.
/// Không giữ lịch, không lưu đơn hàng và không tạo hóa đơn.
/// </summary>
public interface ICheckoutPreparationService
{
    Task<CheckoutPreparationResult> PrepareAsync(
        CheckoutRenterContext renter,
        CheckoutRequest request,
        CancellationToken cancellationToken = default);
}
