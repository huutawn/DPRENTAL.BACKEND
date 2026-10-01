namespace Dprental.InventoryService.Common.Enums;

/// <summary>
/// Trạng thái của lượt đặt lịch (Reservation) trong state machine nghiệp vụ.
/// </summary>
public enum ReservationStatus
{
    /// <summary>
    /// Giữ chỗ tạm thời chờ xác nhận hoặc thanh toán (Hold), có thời hạn hết hạn.
    /// </summary>
    Held = 1,

    /// <summary>
    /// Đã xác nhận thành công (qua xác nhận hold hoặc tạo thủ công).
    /// </summary>
    Confirmed = 2,

    /// <summary>
    /// Thiết bị đã được bàn giao cho khách thuê và đang trong thời gian sử dụng.
    /// </summary>
    InUse = 3,

    /// <summary>
    /// Thiết bị đã được thu hồi và hoàn tất hợp đồng thuê (Terminal state).
    /// </summary>
    Completed = 4,

    /// <summary>
    /// Lượt đặt đã bị hủy trước khi bàn giao thiết bị (Terminal state).
    /// </summary>
    Cancelled = 5,

    /// <summary>
    /// Lượt giữ chỗ đã bị nhả chủ động hoặc hết hạn tự động (Terminal state).
    /// </summary>
    Released = 6
}
