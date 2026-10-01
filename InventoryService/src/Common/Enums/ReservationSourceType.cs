namespace Dprental.InventoryService.Common.Enums;

/// <summary>
/// Nguồn gốc tạo ra lượt đặt lịch.
/// </summary>
public enum ReservationSourceType
{
    /// <summary>
    /// Đặt lịch thủ công do nhân viên hoặc quản trị viên tạo trực tiếp.
    /// </summary>
    Manual = 1,

    /// <summary>
    /// Đặt lịch từ hệ thống bên ngoài (khách thuê đặt qua web/app sàn hoặc đối tác).
    /// </summary>
    External = 2
}
