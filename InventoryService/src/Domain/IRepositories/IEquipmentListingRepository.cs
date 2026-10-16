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
    /// Tìm kiếm và phân trang Listing của người sở hữu kèm derived totalQuantity trong RepeatableRead transaction (2 SELECTs).
    /// </summary>
    Task<OwnerEquipmentListingSearchResult> SearchOwnerAsync(OwnerEquipmentListingSearchCriteria criteria, CancellationToken cancellationToken = default);

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
    /// Thống kê tổng hợp số lượng listing theo equipmentTypeId và brandId trên PostgreSQL.
    /// </summary>
    Task<EquipmentListingStatisticsResult> GetListingStatisticsAsync(EquipmentListingStatisticsCriteria criteria, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách tóm tắt EquipmentListing theo tập ID bằng một truy vấn AsNoTracking, chỉ chọn các trường cần trả phục vụ Rental batch.
    /// </summary>
    Task<IReadOnlyList<EquipmentListingBatchItem>> GetBatchByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách ID các Listing thuộc về người sở hữu theo keyset (ID tăng dần) phục vụ fanout invalidation.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetListingIdsByOwnerKeysetAsync(Guid ownerUserId, Guid? afterListingId, int limit, CancellationToken cancellationToken);
    /// <summary>
    /// Đưa Listing mới vào hàng đợi lưu trữ.
    /// </summary>
    void Add(EquipmentListing listing);

    /// <summary>
    /// Đánh dấu cập nhật thông tin Listing.
    /// </summary>
    void Update(EquipmentListing listing);
}
