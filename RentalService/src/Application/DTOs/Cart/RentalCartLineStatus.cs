namespace Dprental.RentalService.Application.DTOs.Cart;

/// <summary>
/// Trạng thái kiểm tra tính khả dụng và tính hợp lệ của một dòng thiết bị trong giỏ hàng.
/// </summary>
public enum RentalCartLineStatus
{
    /// <summary>Thiết bị hoàn toàn khả dụng với giá và số lượng yêu cầu.</summary>
    Available,
    /// <summary>Không đủ số lượng thiết bị khả dụng trong toàn bộ khoảng thời gian yêu cầu.</summary>
    InsufficientQuantity,
    /// <summary>Thiết bị không khả dụng trong khoảng thời gian yêu cầu.</summary>
    Unavailable,
    /// <summary>Bài niêm yết thiết bị đã bị vô hiệu hóa hoặc gỡ bỏ.</summary>
    ListingDisabled,
    /// <summary>Dữ liệu mục giỏ hàng vi phạm quy tắc xác thực (ví dụ: ngày thuê không hợp lệ).</summary>
    ValidationFailed,
    /// <summary>Ngày bắt đầu thuê đã qua hoặc không còn đủ thời gian đặt trước tối thiểu (3 ngày).</summary>
    DatePassed,
}
