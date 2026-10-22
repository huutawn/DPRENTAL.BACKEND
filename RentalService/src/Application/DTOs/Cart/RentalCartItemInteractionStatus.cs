namespace Dprental.RentalService.Application.DTOs.Cart;

/// <summary>
/// Mức thao tác cho phép trên một dòng mục giỏ hàng.
/// </summary>
public enum RentalCartItemInteractionStatus
{
    /// <summary>Dòng hợp lệ, đủ hàng: Cho phép Checkout, Đổi số lượng, Đổi ngày, Xóa.</summary>
    Enabled,

    /// <summary>Cần sửa số lượng hoặc ngày: Chặn Checkout; Cho phép Đổi số lượng, Đổi ngày, Xóa.</summary>
    DisabledEditable,

    /// <summary>Listing không còn cho thuê hoặc thuộc chính renter: Chặn Checkout, Đổi số lượng, Đổi ngày; Chỉ cho phép Xóa.</summary>
    DisabledReadOnly,
}
