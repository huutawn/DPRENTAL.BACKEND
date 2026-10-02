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
    /// Lấy danh sách tài sản thuộc về một chủ sở hữu có phân trang.
    /// </summary>
    Task<IReadOnlyList<InventoryAsset>> ListByOwnerAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách toàn bộ tài sản trong hệ thống có phân trang (dành cho Admin).
    /// </summary>
    Task<IReadOnlyList<InventoryAsset>> ListAllAsync(int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đưa tài sản mới vào hàng đợi lưu trữ.
    /// </summary>
    void Add(InventoryAsset asset);

    /// <summary>
    /// Đánh dấu cập nhật thông tin tài sản.
    /// </summary>
    void Update(InventoryAsset asset);
}
