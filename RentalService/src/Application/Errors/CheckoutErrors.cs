using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Errors;

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

    public static readonly Error InProgress =
        Error.Conflict("Checkout.InProgress", "Đơn hàng của bạn đang được xử lý. Vui lòng chờ trong giây lát.");

    public static readonly Error IdempotencyKeyReused =
        Error.Conflict("Checkout.IdempotencyKeyReused", "Idempotency-Key này đã được dùng cho một lần gửi đơn khác.");

    /// <summary>Đơn đã được tạo và đang giữ lịch; gửi lại cùng Idempotency-Key để tạo nốt hóa đơn.</summary>
    public static readonly Error InvoiceTemporarilyUnavailable =
        Error.ServiceUnavailable("Checkout.InvoiceTemporarilyUnavailable", "Hệ thống thanh toán đang bận. Đơn hàng đã được tạo và đang giữ lịch, vui lòng thử lại sau ít phút.");

    /// <summary>Payment từ chối tạo hóa đơn; gửi lại cũng không có kết quả khác.</summary>
    public static readonly Error InvoiceRejected =
        Error.Conflict("Checkout.InvoiceRejected", "Không tạo được thanh toán cho đơn hàng này. Đơn hàng sẽ tự hủy khi hết hạn thanh toán.");

    public static readonly Error OrderExpired =
        Error.Expired("Checkout.OrderExpired", "Đơn hàng đã hết hạn thanh toán. Vui lòng đặt lại.");
    /// <summary>Xung đột concurrency token khi lưu trạng thái hóa đơn; yêu cầu nạp lại aggregate trước khi tiếp tục.</summary>
    public static readonly Error InvoicePersistenceConflict =
        Error.Conflict("Checkout.InvoicePersistenceConflict", "Xung đột đồng thời khi lưu thông tin hóa đơn.");


    public static readonly Error DependencyUnavailable =
        Error.ServiceUnavailable("Checkout.DependencyUnavailable", "Chưa kiểm tra được thiết bị. Vui lòng thử lại.");

    public static readonly Error CheckoutNotReady =
        Error.ServiceUnavailable("CHECKOUT_NOT_READY", "Đang xử lý tạo đơn và hóa đơn thanh toán; vui lòng thử lại sau.");
}
