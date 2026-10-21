using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Errors;

public static class RenterAddressErrors
{
    public static readonly Error NotFound =
        Error.NotFound("Address.NotFound", "Không tìm thấy địa chỉ.");

    public static readonly Error LimitReached =
        Error.Conflict("Address.LimitReached", "Sổ địa chỉ đã đủ 10 địa chỉ. Hãy xóa bớt trước khi thêm mới.");

    public static readonly Error Busy =
        Error.Conflict("Address.Busy", "Sổ địa chỉ đang được cập nhật. Vui lòng thử lại.");

    public static Error InvalidRequest(string detail) =>
        Error.Validation("Address.InvalidRequest", detail);
}
