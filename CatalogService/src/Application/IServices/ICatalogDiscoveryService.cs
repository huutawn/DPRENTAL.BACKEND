using Dprental.CatalogService.Application.DTOs;

namespace Dprental.CatalogService.Application.IServices;

public sealed record SearchOutcome(PagedResponse<EquipmentListItemDto>? Page, IReadOnlyList<ValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public interface ICatalogDiscoveryService
{
    Task<SearchOutcome> SearchAsync(EquipmentListQuery query, CancellationToken cancellationToken = default);
    Task<EquipmentDetailDto?> GetDetailAsync(Guid id, CancellationToken cancellationToken = default);
}