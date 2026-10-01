using Dprental.CatalogService.Domain.ValueObjects;

namespace Dprental.CatalogService.Domain.IRepositories;

public interface ICatalogRepository
{
    Task<PagedResult<EquipmentListItem>> GetPublicEquipmentsAsync(EquipmentSearchCriteria criteria, CancellationToken cancellationToken = default);
    Task<EquipmentDetail?> GetPublicEquipmentDetailAsync(Guid id, CancellationToken cancellationToken = default);
}