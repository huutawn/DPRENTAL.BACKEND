using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Errors;

public static class RenterBankAccountErrors
{
    public static readonly Error NotFound =
        Error.NotFound("BankAccount.NotFound", "Không tìm thấy tài khoản ngân hàng.");

    public static readonly Error LimitReached =
        Error.Conflict("BankAccount.LimitReached", "Sổ tài khoản đã đủ 10 tài khoản. Hãy xóa bớt trước khi thêm mới.");

    public static readonly Error Duplicate =
        Error.Conflict("BankAccount.Duplicate", "Tài khoản ngân hàng này đã có trong sổ tài khoản.");

    public static readonly Error Busy =
        Error.Conflict("BankAccount.Busy", "Sổ tài khoản đang được cập nhật. Vui lòng thử lại.");

    public static Error InvalidRequest(string detail) =>
        Error.Validation("BankAccount.InvalidRequest", detail);
}
