using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Repository quản lý kho lưu trữ của các bên cho thuê (Warehouse).
/// Mỗi bên cho thuê (OwnerUserId) sở hữu duy nhất một kho.
/// </summary>
public interface IWarehouseRepository
{
    /// <summary>
    /// Đọc thông tin kho theo OwnerUserId.
    /// </summary>
    Task<Warehouse?> GetByOwnerUserIdAsync(Guid ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kiểm tra kho của bên cho thuê đã tồn tại hay chưa.
    /// </summary>
    Task<bool> ExistsAsync(Guid ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đưa kho mới vào hàng đợi lưu trữ.
    /// </summary>
    void Add(Warehouse warehouse);
}
