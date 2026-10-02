using Dprental.CatalogService.Domain.Entities;

namespace Dprental.CatalogService.Domain.IRepositories;

/// <summary>Kết quả lưu equipment type, phân biệt rõ từng loại xung đột để service trả đúng lỗi.</summary>
public enum EquipmentTypeSaveOutcome
{
    Saved,
    DuplicateCode,
    DuplicateName,

    /// <summary>Type đã bị một request khác sửa sau khi request này đọc nó.</summary>
    ConcurrentUpdate
}

public interface IEquipmentTypeRepository
{
    /// <summary>Tải equipment type kèm category để chỉnh sửa; entity trả về đang được theo dõi thay đổi.</summary>
    Task<EquipmentType?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Kiểm tra mã đã được dùng, bỏ qua chính type đang sửa.</summary>
    Task<bool> CodeExistsAsync(string code, Guid? excludeId = null, CancellationToken cancellationToken = default);

    /// <summary>Kiểm tra tên (đã chuẩn hóa) đã có trong category, bỏ qua chính type đang sửa.</summary>
    Task<bool> NameExistsAsync(
        Guid categoryId,
        string normalizedName,
        Guid? excludeId = null,
        CancellationToken cancellationToken = default);

    Task<Category?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);

    Task AddAsync(EquipmentType equipmentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lưu thay đổi. Xung đột do hai request chạy cùng lúc (trùng unique, hoặc cùng sửa một type)
    /// được trả về dưới dạng outcome thay vì exception.
    /// </summary>
    Task<EquipmentTypeSaveOutcome> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Các type đang Published thuộc category đang active, sắp theo tên; dùng cho catalog public.</summary>
    Task<IReadOnlyList<EquipmentType>> ListPublishedAsync(Guid? categoryId = null, CancellationToken cancellationToken = default);

    /// <summary>Một type đang Published thuộc category đang active; null nếu không công khai.</summary>
    Task<EquipmentType?> GetPublishedAsync(Guid id, CancellationToken cancellationToken = default);
}
