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


    /// <summary>Lấy metadata trạng thái và CurrentSchemaVersion của equipment type mà không tải quan hệ khác.</summary>
    Task<EquipmentTypeMetadataDto?> GetMetadataByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<EquipmentTypeMetadataDto?>(null);
    /// <summary>Lấy chi tiết DTO theo Id cho mọi trạng thái.</summary>
    Task<EquipmentTypeDetailDto?> GetDetailByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>Kiểm tra Code đã tồn tại trong Catalog chưa (phân biệt hoa thường).</summary>
    Task<bool> ExistsByCodeAsync(
        string code,
        Guid? excludeId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Thêm mới EquipmentType ở trạng thái Draft cùng media và optional schema v1 trong cùng transaction.</summary>
    Task<Result<EquipmentTypeDetailDto>> AddAsync(
        EquipmentType equipmentType,
        IReadOnlyList<EquipmentTypeMediaItemDto?>? mediaItems,
        EquipmentTypeSpecDefinition? initialSpecSchema = null,
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

    /// <summary>Lấy danh sách toàn bộ các phiên bản spec schema của equipment type, sắp xếp tăng dần theo Version.</summary>
    Task<IReadOnlyList<EquipmentTypeSpecSchemaDto>> GetSpecSchemasAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default);

    /// <summary>Lấy chi tiết một phiên bản spec schema theo version.</summary>
    Task<EquipmentTypeSpecSchemaDto?> GetSpecSchemaAsync(
        Guid equipmentTypeId,
        int version,
        CancellationToken cancellationToken = default);

    /// <summary>Lấy chi tiết một phiên bản spec schema theo version, tùy chọn chỉ lọc bản đã publish tại database.</summary>
    Task<EquipmentTypeSpecSchemaDto?> GetSpecSchemaAsync(
        Guid equipmentTypeId,
        int version,
        bool publishedOnly,
        CancellationToken cancellationToken = default) =>
        GetSpecSchemaAsync(equipmentTypeId, version, cancellationToken);
    /// <summary>Lấy danh sách các phiên bản spec schema theo danh sách versions, tùy chọn chỉ lọc bản đã publish.</summary>
    Task<IReadOnlyList<EquipmentTypeSpecSchemaDto>> GetSpecSchemasByVersionsAsync(
        Guid equipmentTypeId,
        IEnumerable<int> versions,
        bool publishedOnly,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EquipmentTypeSpecSchemaDto>>(Array.Empty<EquipmentTypeSpecSchemaDto>());
    /// <summary>Lấy entity spec schema nháp hiện tại (PublishedAt == null) của equipment type nếu có.</summary>
    Task<EquipmentTypeSpecSchema?> GetDraftSpecSchemaEntityAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default);

    /// <summary>Lấy entity spec schema theo version để cập nhật hoặc publish.</summary>
    Task<EquipmentTypeSpecSchema?> GetSpecSchemaEntityAsync(
        Guid equipmentTypeId,
        int version,
        CancellationToken cancellationToken = default);

    /// <summary>Lấy version lớn nhất hiện tại của equipment type.</summary>
    Task<int> GetMaxSpecSchemaVersionAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default);

    /// <summary>Tạo mới bản nháp schema kế tiếp, cập nhật UpdatedAt của parent trong cùng transaction.</summary>
    Task<Result<EquipmentTypeSpecSchemaDto>> AddSpecSchemaDraftAsync(
        EquipmentType parent,
        EquipmentTypeSpecDefinition definition,
        CancellationToken cancellationToken = default);

    /// <summary>Cập nhật toàn bộ definition của bản nháp schema, cập nhật UpdatedAt của parent trong cùng transaction.</summary>
    Task<Result<EquipmentTypeSpecSchemaDto>> UpdateSpecSchemaDraftAsync(
        EquipmentType parent,
        EquipmentTypeSpecSchema draftSchema,
        EquipmentTypeSpecDefinition newDefinition,
        CancellationToken cancellationToken = default);

    /// <summary>Publish bản nháp schema và cập nhật CurrentSchemaVersion của parent trong cùng transaction.</summary>
    Task<Result<EquipmentTypeSpecSchemaDto>> PublishSpecSchemaAsync(
        EquipmentType parent,
        EquipmentTypeSpecSchema draftSchema,
        CancellationToken cancellationToken = default);
}
