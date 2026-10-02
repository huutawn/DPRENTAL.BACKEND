using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Hợp đồng repository quản lý aggregate AssetAllocation và các chuyển đổi trạng thái liên quan.
/// </summary>
public interface IAssetAllocationRepository : IRepo<AssetAllocation, Guid>
{
    /// <summary>
    /// Tìm kiếm lượt phân bổ theo ID và giới hạn trong phạm vi tài sản của chủ sở hữu (OwnerUserId).
    /// </summary>
    Task<AssetAllocation?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tìm kiếm lượt phân bổ theo hệ thống nguồn và mã tham chiếu external (Idempotency lookup).
    /// </summary>
    Task<AssetAllocation?> GetByExternalReferenceAsync(string sourceSystem, string externalReference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách các phân bổ ứng viên có khả năng chiếm lịch (Held còn hạn, Allocated) trên tài sản trong khoảng ngày.
    /// </summary>
    Task<IReadOnlyList<AssetAllocation>> GetActiveCandidatesAsync(
        Guid assetId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách các lượt giữ chỗ (Held) đã quá hạn tại thời điểm kiểm tra trên tài sản.
    /// </summary>
    Task<IReadOnlyList<AssetAllocation>> GetExpiredHoldsAsync(
        Guid assetId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách toàn bộ các lượt phân bổ của một tài sản có phân trang.
    /// </summary>
    Task<IReadOnlyList<AssetAllocation>> ListByAssetAsync(
        Guid assetId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách các lượt phân bổ của tất cả tài sản thuộc chủ sở hữu có phân trang.
    /// </summary>
    Task<IReadOnlyList<AssetAllocation>> ListByOwnerAsync(
        Guid ownerUserId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đưa lượt phân bổ mới vào hàng đợi lưu trữ.
    /// </summary>
    void Add(AssetAllocation allocation);

    /// <summary>
    /// Đánh dấu cập nhật lượt phân bổ.
    /// </summary>
    void Update(AssetAllocation allocation);
}
