using Dprental.CatalogService.Application.DTOs;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Application.Mapping;
using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Application.Validators;
using Dprental.CatalogService.Domain.IRepositories;
using Dprental.CatalogService.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace Dprental.CatalogService.Application.Services;

public sealed class CatalogDiscoveryService(
    IEquipmentQueryRepository repository,
    EquipmentListQueryValidator validator,
    IOptions<CatalogDiscoveryOptions> options) : ICatalogDiscoveryService
{
    public async Task<SearchOutcome> SearchAsync(EquipmentListQuery query, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ValidationError> errors = validator.Validate(query);
        if (errors.Count > 0) return new SearchOutcome(null, errors);

        int page = query.Page ?? 1;
        int pageSize = query.PageSize ?? options.Value.DefaultPageSize;
        string? keyword = string.IsNullOrWhiteSpace(query.Keyword) ? null : query.Keyword.Trim();

        EquipmentSearchCriteria criteria = new(
            keyword, query.CategoryId, query.RegionId, query.MinPrice, query.MaxPrice,
            EquipmentListQueryValidator.ParseSort(query.Sort), page, pageSize);

        PagedResult<EquipmentListItem> result = await repository.SearchPublishedAsync(criteria, cancellationToken);
        int totalPages = (int)Math.Ceiling(result.TotalItems / (double)pageSize);

        var response = new PagedResponse<EquipmentListItemDto>(
            result.Items.Select(EquipmentMapper.ToDto).ToList(), page, pageSize, result.TotalItems, totalPages);
        return new SearchOutcome(response, []);
    }

    public async Task<EquipmentDetailDto?> GetDetailAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EquipmentDetail? detail = await repository.GetPublishedByIdAsync(id, cancellationToken);
        return detail is null ? null : EquipmentMapper.ToDto(detail);
    }
}