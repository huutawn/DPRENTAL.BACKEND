using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Hợp đồng repository quản lý vòng đời và truy xuất thông tin tài sản trong kho (InventoryAsset).
/// Bảo vệ dữ liệu theo OwnerUserId.
/// </summary>
public interface IInventoryAssetRepository : IRepo<InventoryAsset, Guid>
{
    /// <summary>
    /// Lấy thông tin tài sản theo ID và giới hạn trong phạm vi người sở hữu (OwnerUserId).
    /// </summary>
    Task<InventoryAsset?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tìm kiếm danh sách tài sản theo criteria phân trang, giới hạn bắt buộc theo OwnerUserId.
    /// </summary>
    Task<IReadOnlyList<InventoryAsset>> SearchAsync(AssetSearchCriteria criteria, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đếm tổng số tài sản khớp với criteria, giới hạn bắt buộc theo OwnerUserId.
    /// </summary>
    Task<int> CountAsync(AssetSearchCriteria criteria, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách các distinct schema_version của tài sản thuộc owner thỏa mãn các bộ lọc metadata.
    /// </summary>
    Task<IReadOnlyList<int>> GetDistinctSchemaVersionsAsync(AssetSearchCriteria criteria, CancellationToken cancellationToken = default);
    /// <summary>
    /// Lấy snapshot tài sản cùng các khoảng chặn lịch (CalendarBlocks) và các lượt phân bổ ứng viên (Held/Allocated)
    /// giao với cửa sổ ngày [startDate, endDate] phục vụ đọc lịch tổng hợp (Calendar Read) trong một single query snapshot.
    /// </summary>
    Task<InventoryAsset?> GetCalendarSnapshotAsync(
        Guid assetId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy snapshot hàng loạt tài sản cùng các khoảng chặn lịch (CalendarBlocks) và các lượt phân bổ ứng viên (Held/Allocated)
    /// giao với cửa sổ ngày [startDate, endDate] phục vụ kiểm tra khả dụng (Availability) và tính toán busy ranges.
    /// Tải assets, overlapping CalendarBlocks và Held/Allocated candidates bằng filtered EF split query (tối đa 3 SELECTs)
    /// trong transaction đọc RepeatableRead.
    /// </summary>
    Task<IReadOnlyList<InventoryAsset>> GetAvailabilitySnapshotsAsync(
        IReadOnlyList<Guid> assetIds,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// Đưa tài sản mới vào hàng đợi lưu trữ.
    /// </summary>
    void Add(InventoryAsset asset);

    /// <summary>
    /// Đánh dấu cập nhật thông tin tài sản.
    /// </summary>
    void Update(InventoryAsset asset);
}
