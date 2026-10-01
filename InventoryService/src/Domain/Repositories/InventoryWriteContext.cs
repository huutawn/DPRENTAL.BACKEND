using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai IInventoryWriteContext cung cấp các repository dùng chung một scoped DbContext trong giao dịch.
/// Đảm bảo tất cả các aggregate được đọc và ghi trong cùng một transaction boundary.
/// </summary>
public sealed class InventoryWriteContext : IInventoryWriteContext
{
    private readonly InventoryServiceDbContext _context;

    /// <summary>
    /// Khởi tạo bối cảnh thực thi ghi với DbContext và các repository liên quan.
    /// </summary>
    /// <param name="context">DbContext độc quyền của transaction.</param>
    /// <param name="equipments">Repository thiết bị.</param>
    /// <param name="reservations">Repository lượt đặt lịch.</param>
    /// <param name="calendarBlocks">Repository khoảng chặn lịch.</param>
    /// <param name="damageReports">Repository báo cáo hư hỏng.</param>
    /// <param name="notifications">Repository thông báo tổ chức.</param>
    public InventoryWriteContext(
        InventoryServiceDbContext context,
        Guid equipmentId,
        IInventoryEquipmentRepository equipments,
        IReservationRepository reservations,
        ICalendarBlockRepository calendarBlocks,
        IDamageReportRepository damageReports,
        INotificationRepository notifications)
    {
        _context = context;
        EquipmentId = equipmentId;
        Equipments = equipments;
        Reservations = reservations;
        CalendarBlocks = calendarBlocks;
        DamageReports = damageReports;
        Notifications = notifications;
    }

    /// <inheritdoc />
    public Guid EquipmentId { get; }

    public IInventoryEquipmentRepository Equipments { get; }

    /// <inheritdoc />
    public IReservationRepository Reservations { get; }

    /// <inheritdoc />
    public ICalendarBlockRepository CalendarBlocks { get; }

    /// <inheritdoc />
    public IDamageReportRepository DamageReports { get; }

    /// <inheritdoc />
    public INotificationRepository Notifications { get; }

    /// <inheritdoc />
    public void StageOutbox(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _context.OutboxMessages.Add(message);
    }
}
