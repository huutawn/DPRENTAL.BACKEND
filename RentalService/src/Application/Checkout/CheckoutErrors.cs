using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Checkout;

public static class CheckoutErrors
{
    public static readonly Error Unauthenticated =
        Error.Unauthorized("Checkout.Unauthenticated", "Yêu cầu đăng nhập để gửi đơn hàng.");

    public static readonly Error ProfileIncomplete =
        Error.Forbidden("Checkout.ProfileIncomplete", "Bạn cần hoàn thiện hồ sơ trước khi gửi đơn hàng.");

    public static readonly Error InvalidRequest =
        Error.Validation("Checkout.InvalidRequest", "Thông tin gửi đơn hàng không hợp lệ.");

    public static readonly Error TermsVersionOutdated =
        Error.Conflict("Checkout.TermsVersionOutdated", "Điều khoản thuê đã có phiên bản mới. Vui lòng đọc và đồng ý lại.");

    public static readonly Error PendingOrderExists =
        Error.Conflict("Checkout.PendingOrderExists", "Bạn đang có đơn hàng chưa thanh toán. Hãy thanh toán hoặc hủy đơn hàng đó trước.");

    public static readonly Error InvalidRentalDates =
        Error.Validation("Checkout.InvalidRentalDates", "Ngày thuê của một số thiết bị không còn hợp lệ.");

    public static readonly Error ItemsUnavailable =
        Error.Conflict("Checkout.ItemsUnavailable", "Một số thiết bị đã chọn không còn cho thuê hoặc không đủ số lượng.");

    public static readonly Error PriceChanged =
        Error.Conflict("Checkout.PriceChanged", "Giá vừa thay đổi. Vui lòng xem lại và gửi lại.");

    public static readonly Error HoldFailed =
        Error.Conflict("Checkout.HoldFailed", "Không giữ được lịch cho toàn bộ thiết bị đã chọn. Chưa có đơn hàng nào được tạo.");

    public static readonly Error OrderNotSaved =
        Error.Conflict("Checkout.OrderNotSaved", "Chưa tạo được đơn hàng. Vui lòng gửi lại.");

    public static readonly Error DependencyUnavailable =
        Error.ServiceUnavailable("Checkout.DependencyUnavailable", "Chưa kiểm tra được thiết bị. Vui lòng thử lại.");
}
