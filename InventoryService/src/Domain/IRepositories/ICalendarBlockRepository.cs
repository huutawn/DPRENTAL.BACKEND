using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Hợp đồng repository quản lý các khoảng thời gian chặn lịch của tài sản (CalendarBlock).
/// </summary>
public interface ICalendarBlockRepository : IRepo<CalendarBlock, Guid>
{
    /// <summary>
    /// Lấy danh sách các khoảng chặn lịch bị trùng lặp với khoảng thời gian truy vấn trên tài sản.
    /// </summary>
    Task<IReadOnlyList<CalendarBlock>> GetOverlappingBlocksAsync(
        Guid assetId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy toàn bộ danh sách khoảng chặn lịch của một tài sản có phân trang.
    /// </summary>
    Task<IReadOnlyList<CalendarBlock>> ListByAssetAsync(
        Guid assetId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đưa khoảng chặn lịch mới vào hàng đợi lưu trữ.
    /// </summary>
    void Add(CalendarBlock block);

    /// <summary>
    /// Đưa khoảng chặn lịch vào danh sách xóa (gỡ chặn lịch).
    /// </summary>
    void Remove(CalendarBlock block);
}
