using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Bối cảnh thực thi ghi trong phạm vi giao dịch phân tán độc quyền của thiết bị.
/// Cung cấp quyền truy cập tới các repository dùng chung DbContext đã được bảo vệ bởi advisory lock.
/// </summary>
public interface IInventoryWriteContext
{
    /// <summary>Thiết bị được khóa độc quyền cho toàn bộ operation.</summary>
    Guid EquipmentId { get; }
    /// <summary>
    /// Repository quản lý thiết bị trong kho.
    /// </summary>
    IInventoryEquipmentRepository Equipments { get; }

    /// <summary>
    /// Repository quản lý các lượt đặt lịch thuê.
    /// </summary>
    IReservationRepository Reservations { get; }

    /// <summary>
    /// Repository quản lý khoảng thời gian chặn lịch thiết bị.
    /// </summary>
    ICalendarBlockRepository CalendarBlocks { get; }

    /// <summary>
    /// Repository quản lý các báo cáo hư hỏng thiết bị.
    /// </summary>
    IDamageReportRepository DamageReports { get; }

    /// <summary>
    /// Repository quản lý các thông báo vận hành của tổ chức.
    /// </summary>
    INotificationRepository Notifications { get; }

    /// <summary>
    /// Đưa thông điệp Outbox vào hàng đợi lưu trữ trong cùng một transaction.
    /// </summary>
    /// <param name="message">Thông điệp Outbox cần phát hành.</param>
    void StageOutbox(OutboxMessage message);
}
