using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Errors;

public static class OrderQueryErrors
{
    public static readonly Error Unauthenticated =
        Error.Unauthorized("Order.Unauthenticated", "Yêu cầu đăng nhập để xem đơn hàng.");

    public static readonly Error PersonalAccountRequired =
        Error.Forbidden("Order.PersonalAccountRequired", "Chỉ tài khoản cá nhân mới xem được đơn hàng.");

    // Đơn của người khác cũng trả lỗi này để không lộ việc mã đơn có tồn tại hay không.
    public static readonly Error OrderNotFound =
        Error.NotFound("Order.NotFound", "Không tìm thấy đơn hàng.");

    // Đơn con của nhà cung cấp khác hoặc đơn chưa thanh toán cũng trả lỗi này.
    public static readonly Error SubOrderNotFound =
        Error.NotFound("SubOrder.NotFound", "Không tìm thấy đơn con.");
}
