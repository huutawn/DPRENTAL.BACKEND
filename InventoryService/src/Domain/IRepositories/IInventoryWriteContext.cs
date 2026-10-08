using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Bối cảnh thực thi ghi trong phạm vi giao dịch phân tán độc quyền của Listing và Asset.
/// Cung cấp quyền truy cập tới các repository dùng chung DbContext đã được bảo vệ bởi advisory locks.
/// </summary>
public interface IInventoryWriteContext
{
    /// <summary>
    /// Tài sản được khóa độc quyền (nếu là transaction cấp đơn vị tài sản).
    /// </summary>
    Guid AssetId { get; }

    /// <summary>
    /// Listing được khóa độc quyền trong transaction boundary.
    /// </summary>
    Guid? ListingId { get; }

    /// <summary>
    /// Repository quản lý danh mục niêm yết thiết bị.
    /// </summary>
    IEquipmentListingRepository Listings { get; }

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
    /// Khóa bổ sung các đơn vị tài sản vật lý theo thứ tự ID tăng dần trong cùng transaction boundary.
    /// </summary>
    Task AcquireAssetLocksAsync(IEnumerable<Guid> assetIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đưa thông điệp Outbox vào hàng đợi lưu trữ trong cùng một transaction.
    /// </summary>
    void StageOutbox(OutboxMessage message);
}
