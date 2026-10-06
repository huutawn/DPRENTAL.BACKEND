using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;

namespace Dprental.CatalogService.Domain.IRepositories;

public interface IEquipmentTypeRepository
{
    /// <summary>LuÃ´n Ä‘á»c Status = Published. Filter, sort á»•n Ä‘á»‹nh, phÃ¢n trang vÃ  projection Ä‘á»u á»Ÿ database.</summary>
    Task<PagedResult<EquipmentTypeListItemDto>> SearchPublishedAsync(
        GetEquipmentTypesRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Danh sÃ¡ch quáº£n trá»‹: Ä‘á»c má»i tráº¡ng thÃ¡i, hoáº·c chá»‰ cÃ¡c tráº¡ng thÃ¡i trong <paramref name="statuses"/> náº¿u khÃ¡c rá»—ng.
    /// Filter, sort á»•n Ä‘á»‹nh, phÃ¢n trang vÃ  projection Ä‘á»u á»Ÿ database.
    /// </summary>
    Task<PagedResult<EquipmentTypeAdminListItemDto>> SearchAdminAsync(
        GetAdminEquipmentTypesRequest request,
        IReadOnlyList<EquipmentTypeStatus> statuses,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PagedResult<EquipmentTypeAdminListItemDto>([], request.Page, request.PageSize, 0));

    /// <summary>Tráº£ null náº¿u khÃ´ng tá»“n táº¡i hoáº·c khÃ´ng Published (Draft/Disabled khÃ´ng lá»™ ra khi Ä‘oÃ¡n id).</summary>
    Task<EquipmentTypeDetailDto?> GetPublishedByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>Láº¥y entity theo Id kÃ¨m Category vÃ  Media phá»¥c vá»¥ cáº­p nháº­t hoáº·c chuyá»ƒn tráº¡ng thÃ¡i.</summary>
    Task<EquipmentType?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);


    /// <summary>Láº¥y metadata tráº¡ng thÃ¡i vÃ  CurrentSchemaVersion cá»§a equipment type mÃ  khÃ´ng táº£i quan há»‡ khÃ¡c.</summary>
    Task<EquipmentTypeMetadataDto?> GetMetadataByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<EquipmentTypeMetadataDto?>(null);
    /// <summary>Láº¥y chi tiáº¿t DTO theo Id cho má»i tráº¡ng thÃ¡i.</summary>
    Task<EquipmentTypeDetailDto?> GetDetailByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>Kiá»ƒm tra Code Ä‘Ã£ tá»“n táº¡i trong Catalog chÆ°a (phÃ¢n biá»‡t hoa thÆ°á»ng).</summary>
    Task<bool> ExistsByCodeAsync(
        string code,
        Guid? excludeId = null,
        CancellationToken cancellationToken = default);

    /// <summary>ThÃªm má»›i EquipmentType á»Ÿ tráº¡ng thÃ¡i Draft cÃ¹ng media references trong cÃ¹ng transaction.</summary>
    Task<EquipmentType?> GetWithSchemaAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<EquipmentType?>(null);
/// <summary>ThÃªm má»›i EquipmentType á»Ÿ tráº¡ng thÃ¡i Draft cÃ¹ng media vÃ  optional schema v1 trong cÃ¹ng transaction.</summary>
    Task<Result<EquipmentTypeDetailDto>> AddAsync(
        EquipmentType equipmentType,
        IReadOnlyList<EquipmentTypeMediaItemDto?>? mediaItems,
        EquipmentTypeSpecDefinition? initialSpecSchema = null,
        CancellationToken cancellationToken = default);

    /// <summary>LÆ°u cÃ¡c thay Ä‘á»•i canonical vÃ  danh sÃ¡ch media mong muá»‘n cá»§a entity Ä‘Ã£ load.</summary>
    Task<Result<EquipmentTypeDetailDto>> SaveAsync(
        EquipmentType entity,
        IReadOnlyList<EquipmentTypeMediaItemDto?>? desiredMedia,
        CancellationToken cancellationToken = default);

    /// <summary>LÆ°u thay Ä‘á»•i tráº¡ng thÃ¡i entity (Publish / Disable) vÃ  tráº£ response tá»« aggregate in-memory.</summary>
    Task<Result<EquipmentTypeDetailDto>> SaveStateTransitionAsync(
        EquipmentType entity,
        CancellationToken cancellationToken = default);

    /// <summary>Láº¥y danh sÃ¡ch toÃ n bá»™ cÃ¡c phiÃªn báº£n spec schema cá»§a equipment type, sáº¯p xáº¿p tÄƒng dáº§n theo Version.</summary>
    Task<IReadOnlyList<EquipmentTypeSpecSchemaDto>> GetSpecSchemasAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default);

    /// <summary>Láº¥y chi tiáº¿t má»™t phiÃªn báº£n spec schema theo version.</summary>
    Task<EquipmentTypeSpecSchemaDto?> GetSpecSchemaAsync(
        Guid equipmentTypeId,
        int version,
        CancellationToken cancellationToken = default);

    /// <summary>Láº¥y chi tiáº¿t má»™t phiÃªn báº£n spec schema theo version, tÃ¹y chá»n chá»‰ lá»c báº£n Ä‘Ã£ publish táº¡i database.</summary>
    Task<EquipmentTypeSpecSchemaDto?> GetSpecSchemaAsync(
        Guid equipmentTypeId,
        int version,
        bool publishedOnly,
        CancellationToken cancellationToken = default) =>
        GetSpecSchemaAsync(equipmentTypeId, version, cancellationToken);
    /// <summary>Láº¥y danh sÃ¡ch cÃ¡c phiÃªn báº£n spec schema theo danh sÃ¡ch versions, tÃ¹y chá»n chá»‰ lá»c báº£n Ä‘Ã£ publish.</summary>
    Task<IReadOnlyList<EquipmentTypeSpecSchemaDto>> GetSpecSchemasByVersionsAsync(
        Guid equipmentTypeId,
        IEnumerable<int> versions,
        bool publishedOnly,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EquipmentTypeSpecSchemaDto>>(Array.Empty<EquipmentTypeSpecSchemaDto>());
    /// <summary>Láº¥y entity spec schema nhÃ¡p hiá»‡n táº¡i (PublishedAt == null) cá»§a equipment type náº¿u cÃ³.</summary>
    Task<EquipmentTypeSpecSchema?> GetDraftSpecSchemaEntityAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default);

    /// <summary>Láº¥y entity spec schema theo version Ä‘á»ƒ cáº­p nháº­t hoáº·c publish.</summary>
    Task<EquipmentTypeSpecSchema?> GetSpecSchemaEntityAsync(
        Guid equipmentTypeId,
        int version,
        CancellationToken cancellationToken = default);

    Task<EquipmentTypeSpecSchema?> GetSpecSchemaEntityByIdAsync(
        Guid schemaId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<EquipmentTypeSpecSchema?>(null);

    /// <summary>Láº¥y version lá»›n nháº¥t hiá»‡n táº¡i cá»§a equipment type.</summary>
    Task<int> GetMaxSpecSchemaVersionAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default);

    /// <summary>Táº¡o má»›i báº£n nhÃ¡p schema káº¿ tiáº¿p, cáº­p nháº­t UpdatedAt cá»§a parent trong cÃ¹ng transaction.</summary>
    Task<Result<EquipmentTypeSpecSchemaDto>> AddSpecSchemaDraftAsync(
        EquipmentType parent,
        EquipmentTypeSpecDefinition definition,
        CancellationToken cancellationToken = default);

    /// <summary>Cáº­p nháº­t toÃ n bá»™ definition cá»§a báº£n nhÃ¡p schema, cáº­p nháº­t UpdatedAt cá»§a parent trong cÃ¹ng transaction.</summary>
    Task<Result<EquipmentTypeSpecSchemaDto>> UpdateSpecSchemaDraftAsync(
        EquipmentType parent,
        EquipmentTypeSpecSchema draftSchema,
        EquipmentTypeSpecDefinition newDefinition,
        CancellationToken cancellationToken = default);

    /// <summary>Publish báº£n nhÃ¡p schema vÃ  cáº­p nháº­t CurrentSchemaVersion cá»§a parent trong cÃ¹ng transaction.</summary>
    Task<Result<EquipmentTypeSpecSchemaDto>> PublishSpecSchemaAsync(
        EquipmentType parent,
        EquipmentTypeSpecSchema draftSchema,
        CancellationToken cancellationToken = default);
}
