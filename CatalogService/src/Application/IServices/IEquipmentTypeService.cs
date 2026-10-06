using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

namespace Dprental.CatalogService.Application.IServices;

public interface IEquipmentTypeService
{
    Task<Result<PagedResult<EquipmentTypeListItemDto>>> SearchAsync(
        GetEquipmentTypesRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Danh sách quản trị ở mọi trạng thái, có lọc theo status.</summary>
    Task<Result<PagedResult<EquipmentTypeAdminListItemDto>>> SearchAdminAsync(
        GetAdminEquipmentTypesRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Chi tiết cho quản trị ở mọi trạng thái, kể cả Draft và Disabled.</summary>
    Task<Result<EquipmentTypeDetailDto>> GetAdminDetailAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypeDetailDto>> GetPublishedDetailAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypeDetailDto>> CreateAsync(
        CreateEquipmentTypeRequest request,
        string operatorUserId = "",
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypeDetailDto>> UpdateAsync(
        Guid id,
        UpdateEquipmentTypeRequest request,
        string operatorUserId = "",
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypeDetailDto>> PublishAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypeDetailDto>> DisableAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<EquipmentTypeSpecSchemaDto>>> GetSpecSchemasAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypeSpecSchemaDto>> GetSpecSchemaAsync(
        Guid equipmentTypeId,
        int version,
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypeSpecSchemaDto>> CreateSpecSchemaDraftAsync(
        Guid equipmentTypeId,
        EquipmentTypeSpecDefinition definition,
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypeSpecSchemaDto>> UpdateSpecSchemaDraftAsync(
        Guid equipmentTypeId,
        int version,
        EquipmentTypeSpecDefinition definition,
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypeSpecSchemaDto>> PublishSpecSchemaAsync(
        Guid equipmentTypeId,
        int version,
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypePublishedSchemaResponse>> GetCurrentPublishedSchemaAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypePublishedSchemaResponse>> GetPublishedSchemaByVersionAsync(
        Guid equipmentTypeId,
        int version,
        CancellationToken cancellationToken = default);


    Task<Result<EquipmentTypeBatchSchemasResponse>> GetSchemasBatchAsync(
        Guid equipmentTypeId,
        EquipmentTypeBatchSchemasRequest request,
        CancellationToken cancellationToken = default);
    Task<Result<SpecificationValidationResult>> ValidateSpecificationAsync(
        Guid equipmentTypeId,
        ValidateSpecificationRequest request,
        CancellationToken cancellationToken = default);
}
