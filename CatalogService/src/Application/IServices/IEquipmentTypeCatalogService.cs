using Dprental.CatalogService.Application.DTOs.EquipmentType;
using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.IServices;

/// <summary>Đọc công khai các equipment type đang Published.</summary>
public interface IEquipmentTypeCatalogService
{
    Task<IReadOnlyList<EquipmentTypeResponse>> ListPublishedAsync(
        Guid? categoryId = null,
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentTypeResponse>> GetPublishedAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default);
}
