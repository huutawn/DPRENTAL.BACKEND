using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Errors;

public static class RentalRefundErrors
{
    public static readonly Error NotFound =
        Error.NotFound("Refund.NotFound", "Dòng thiết bị không có khoản nào cần hoàn cho người thuê.");

    public static readonly Error AlreadyConfirmed =
        Error.Conflict("Refund.AlreadyConfirmed", "Khoản hoàn này đã được xác nhận chuyển khoản với mã giao dịch khác.");

    public static readonly Error DuplicateBankTransactionRef =
        Error.Conflict("Refund.DuplicateBankTransactionRef", "Mã giao dịch ngân hàng này đã được dùng cho một khoản hoàn khác.");

    public static readonly Error Busy =
        Error.Conflict("Refund.Busy", "Đơn đang được cập nhật. Vui lòng thử lại.");

    public static Error InvalidRequest(string detail) =>
        Error.Validation("Refund.InvalidRequest", detail);
}
