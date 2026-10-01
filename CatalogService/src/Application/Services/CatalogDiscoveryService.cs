using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Application.Validators;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.Equipments;
using Dprental.CatalogService.Domain.IRepositories;

namespace Dprental.CatalogService.Application.Services;

public sealed class CatalogDiscoveryService(
    IEquipmentQueryRepository repository,
    EquipmentListQueryValidator validator) : ICatalogDiscoveryService
{
    public async Task<Result<PagedResult<EquipmentListItemDto>>> SearchAsync(
        GetEquipmentsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result validationResult = validator.Validate(request);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        PagedResult<EquipmentListItemDto> pagedResult = await repository.SearchPublishedAsync(request, cancellationToken);
        return pagedResult;
    }

    public async Task<Result<EquipmentDetailDto>> GetDetailAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EquipmentDetailDto? detail = await repository.GetPublishedByIdAsync(id, cancellationToken);
        if (detail is null)
        {
            return CatalogErrors.Equipment.NotFound;
        }

        return detail;
    }
}
