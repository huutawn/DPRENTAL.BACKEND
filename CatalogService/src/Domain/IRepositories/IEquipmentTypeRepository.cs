using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;

namespace Dprental.CatalogService.Domain.IRepositories;

public interface IEquipmentTypeRepository
{
    /// <summary>Luôn đọc Status = Published. Filter, sort ổn định, phân trang và projection đều ở database.</summary>
    Task<PagedResult<EquipmentTypeListItemDto>> SearchPublishedAsync(
        GetEquipmentTypesRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Trả null nếu không tồn tại hoặc không Published (Draft/Disabled không lộ ra khi đoán id).</summary>
    Task<EquipmentTypeDetailDto?> GetPublishedByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>Lấy entity theo Id kèm Category và Media phục vụ cập nhật hoặc chuyển trạng thái.</summary>
    Task<EquipmentType?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>Lấy chi tiết DTO theo Id cho mọi trạng thái.</summary>
    Task<EquipmentTypeDetailDto?> GetDetailByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>Kiểm tra Code đã tồn tại trong Catalog chưa (phân biệt hoa thường).</summary>
    Task<bool> ExistsByCodeAsync(
        string code,
        Guid? excludeId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Thêm mới EquipmentType ở trạng thái Draft cùng media references trong cùng transaction.</summary>
    Task<EquipmentType?> GetWithSchemaAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<EquipmentTypeDetailDto>> AddAsync(
        EquipmentType equipmentType,
        IReadOnlyList<EquipmentTypeMediaItemDto?>? mediaItems,
        CancellationToken cancellationToken = default);

    /// <summary>Lưu các thay đổi canonical và danh sách media mong muốn của entity đã load.</summary>
    Task<Result<EquipmentTypeDetailDto>> SaveAsync(
        EquipmentType entity,
        IReadOnlyList<EquipmentTypeMediaItemDto?>? desiredMedia,
        CancellationToken cancellationToken = default);

    /// <summary>Lưu thay đổi trạng thái entity (Publish / Disable) và trả response từ aggregate in-memory.</summary>
    Task<Result<EquipmentTypeDetailDto>> SaveStateTransitionAsync(
        EquipmentType entity,
        CancellationToken cancellationToken = default);
}
