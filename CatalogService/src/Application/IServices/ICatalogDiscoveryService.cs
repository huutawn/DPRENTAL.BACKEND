using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.Equipments;

namespace Dprental.CatalogService.Application.IServices;

public interface ICatalogDiscoveryService
{
    Task<Result<PagedResult<EquipmentListItemDto>>> SearchAsync(
        GetEquipmentsRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<EquipmentDetailDto>> GetDetailAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
