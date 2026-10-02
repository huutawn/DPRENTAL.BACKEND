using Dprental.CatalogService.Application.DTOs.EquipmentType;
using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.IServices;

/// <summary>
/// Vòng đời quản lý equipment type của Admin: tạo, sửa, publish, disable.
/// Việc "người gọi có phải Admin không" được chặn ở API boundary bằng authorization policy.
/// </summary>
public interface IEquipmentTypeManagementService
{
    Task<Result<EquipmentTypeManagementResponse>> CreateAsync(
        CreateEquipmentTypeRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypeManagementResponse>> UpdateAsync(
        Guid equipmentTypeId,
        UpdateEquipmentTypeRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypeManagementResponse>> PublishAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypeManagementResponse>> DisableAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypeManagementResponse>> GetForManagementAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default);
}
