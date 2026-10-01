namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>Hợp đồng đọc theo định danh; thao tác ghi thuộc repository nghiệp vụ và write transaction.</summary>
public interface IRepo<TEntity, in TKey> where TEntity : class
{
    /// <summary>Đọc entity theo ID; trả về null khi không tồn tại. Không thay thế truy vấn giới hạn tổ chức.</summary>
    Task<TEntity?> GetByIdAsync(TKey id, CancellationToken cancellationToken = default);
}
