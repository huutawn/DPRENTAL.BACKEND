namespace Dprental.InventoryService.Common.Enums;

/// <summary>
/// Phân loại thông báo vận hành gửi đến tổ chức.
/// </summary>
public enum NotificationType
{
    /// <summary>
    /// Thông báo lượt giữ chỗ đã hết hạn do không được xác nhận kịp thời.
    /// </summary>
    HoldExpired = 1,

    /// <summary>
    /// Thông báo lượt đặt lịch đã bị hủy.
    /// </summary>
    ReservationCancelled = 2,

    /// <summary>
    /// Thông báo đến hạn bàn giao thiết bị cho khách thuê.
    /// </summary>
    DeliveryDue = 3,

    /// <summary>
    /// Thông báo thiết bị quá hạn thu hồi chưa được hoàn trả.
    /// </summary>
    ReturnOverdue = 4,

    /// <summary>
    /// Thông báo có báo cáo sự cố hư hỏng mới từ khách thuê.
    /// </summary>
    DamageReportSubmitted = 5,

    /// <summary>
    /// Thông báo vận hành chung khác.
    /// </summary>
    General = 6
}
