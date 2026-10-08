using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Hợp đồng repository quản lý danh mục niêm yết thiết bị (EquipmentListing).
/// Hỗ trợ truy vấn owner, khám phá công khai (Public Discovery) và calendar snapshot.
/// </summary>
public interface IEquipmentListingRepository : IRepo<EquipmentListing, Guid>
{
    /// <summary>
    /// Lấy thông tin Listing theo ID giới hạn trong phạm vi người sở hữu (OwnerUserId).
    /// </summary>
    Task<EquipmentListing?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy thông tin Listing công khai theo ID (chỉ listing có Status = Active).
    /// </summary>
    Task<EquipmentListing?> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách Listing của người sở hữu kèm phân trang.
    /// </summary>
    Task<IReadOnlyList<EquipmentListing>> ListForOwnerAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đếm tổng số Listing thuộc người sở hữu.
    /// </summary>
    Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy tổng số lượng thiết bị vật lý đang nhận đặt lịch (AcceptingBookings == true) của một Listing.
    /// </summary>
    Task<int> GetTotalQuantityAsync(Guid listingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách các distinct schema_version của các Listing công khai thỏa mãn bộ lọc metadata.
    /// </summary>
    Task<IReadOnlyList<int>> GetDistinctSchemaVersionsPublicAsync(AssetSearchFilters filters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tìm kiếm và phân trang Listing công khai kèm tính toán availability trong RepeatableRead transaction.
    /// </summary>
    Task<PublicEquipmentListingSearchResult> SearchPublicAsync(EquipmentListingSearchCriteria criteria, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy snapshot Listing cùng toàn bộ units vật lý, calendar blocks và allocations trong cửa sổ ngày [startDate, endDate].
    /// </summary>
    Task<EquipmentListing?> GetCalendarSnapshotAsync(Guid listingId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đưa Listing mới vào hàng đợi lưu trữ.
    /// </summary>
    void Add(EquipmentListing listing);

    /// <summary>
    /// Đánh dấu cập nhật thông tin Listing.
    /// </summary>
    void Update(EquipmentListing listing);
}
