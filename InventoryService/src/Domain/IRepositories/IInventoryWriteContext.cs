using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Bối cảnh thực thi ghi trong phạm vi giao dịch phân tán độc quyền của tài sản.
/// Cung cấp quyền truy cập tới các repository dùng chung DbContext đã được bảo vệ bởi advisory lock.
/// </summary>
public interface IInventoryWriteContext
{
    /// <summary>
    /// Tài sản được khóa độc quyền cho toàn bộ operation.
    /// </summary>
    Guid AssetId { get; }

    /// <summary>
    /// Repository quản lý tài sản trong kho.
    /// </summary>
    IInventoryAssetRepository Assets { get; }

    /// <summary>
    /// Repository quản lý các lượt phân bổ tài sản.
    /// </summary>
    IAssetAllocationRepository Allocations { get; }

    /// <summary>
    /// Repository quản lý khoảng thời gian chặn lịch tài sản.
    /// </summary>
    ICalendarBlockRepository CalendarBlocks { get; }

    /// <summary>
    /// Đưa thông điệp Outbox vào hàng đợi lưu trữ trong cùng một transaction.
    /// </summary>
    void StageOutbox(OutboxMessage message);
}
