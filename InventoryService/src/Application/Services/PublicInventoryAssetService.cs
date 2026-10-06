using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Application.Services;

/// <summary>
/// Dịch vụ ứng dụng tìm kiếm, lọc và đọc chi tiết tài sản công khai (Public Asset Discovery).
/// Vận hành ẩn danh và độc lập với thông tin định danh caller.
/// </summary>
public sealed class PublicInventoryAssetService : IPublicInventoryAssetService
{
    private readonly IInventoryAssetRepository _assetRepository;
    private readonly AssetSearchQueryValidator _queryValidator;
    private readonly AssetSearchFilterResolver _filterResolver;
    private readonly MediaServiceOptions _mediaOptions;
    private readonly TimeProvider _timeProvider;

    public PublicInventoryAssetService(
        IInventoryAssetRepository assetRepository,
        AssetSearchQueryValidator queryValidator,
        AssetSearchFilterResolver filterResolver,
        IOptions<MediaServiceOptions> mediaOptions,
        TimeProvider? timeProvider = null)
    {
        _assetRepository = assetRepository ?? throw new ArgumentNullException(nameof(assetRepository));
        _queryValidator = queryValidator ?? throw new ArgumentNullException(nameof(queryValidator));
        _filterResolver = filterResolver ?? throw new ArgumentNullException(nameof(filterResolver));
        ArgumentNullException.ThrowIfNull(mediaOptions);
        _mediaOptions = mediaOptions.Value ?? throw new ArgumentException("MediaServiceOptions không được null.", nameof(mediaOptions));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<Result<PublicInventoryAssetResponse>> GetPublicAssetByIdAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            return Error.NotFound("ASSET_NOT_FOUND", "Tài sản không tồn tại.");
        }

        var asset = await _assetRepository.GetPublicByIdAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return Error.NotFound("ASSET_NOT_FOUND", "Tài sản không tồn tại.");
        }

        var response = PublicInventoryAssetMapper.ToResponse(asset, _mediaOptions.PublicGatewayUrl);
        return Result.Success(response);
    }

    /// <inheritdoc />
    public async Task<Result<PagedResponse<PublicInventoryAssetResponse>>> ListPublicAssetsAsync(
        ListPublicAssetsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var valResult = _queryValidator.Validate(
            request.Page,
            request.PageSize,
            request.Q,
            request.EquipmentTypeId,
            request.BrandId,
            request.ManufactureYearFrom,
            request.ManufactureYearTo,
            request.Sort);

        if (valResult.IsFailure)
        {
            return valResult.Error;
        }

        var validated = valResult.Value;

        DateOnly? availabilityFrom = null;
        DateOnly? availabilityTo = null;

        if (request.From.HasValue || request.To.HasValue)
        {
            var dateValidationResult = AvailabilityDateValidator.ValidateDateWindow(
                request.From,
                request.To,
                out var validStart,
                out var validEnd);

            if (dateValidationResult.IsFailure)
            {
                return dateValidationResult.Error;
            }

            availabilityFrom = validStart;
            availabilityTo = validEnd;
        }

        var resolveResult = await _filterResolver.ResolveAsync(
            request.Filters,
            validated.EquipmentTypeId,
            ct => _assetRepository.GetDistinctSchemaVersionsPublicAsync(
                new AssetSearchFilters(
                    validated.SearchKeyword,
                    validated.EquipmentTypeId,
                    validated.BrandId,
                    validated.ManufactureYearFrom,
                    validated.ManufactureYearTo),
                ct),
            isFieldForbidden: PublicAssetDataPolicy.IsReservedPropertyName,
            cancellationToken);

        if (resolveResult.IsFailure)
        {
            return resolveResult.Error;
        }

        var resolvedFilters = resolveResult.Value;

        DateTimeOffset? evaluationNowUtc = null;
        if (availabilityFrom.HasValue && availabilityTo.HasValue)
        {
            evaluationNowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
        }

        var searchFilters = new AssetSearchFilters(
            SearchKeyword: validated.SearchKeyword,
            EquipmentTypeId: validated.EquipmentTypeId,
            BrandId: validated.BrandId,
            ManufactureYearFrom: validated.ManufactureYearFrom,
            ManufactureYearTo: validated.ManufactureYearTo,
            Sort: validated.Sort,
            CompatibleSchemaVersions: resolvedFilters.CompatibleSchemaVersions,
            DynamicFilters: resolvedFilters.Criteria);

        var searchCriteria = new PublicAssetSearchCriteria(
            filters: searchFilters,
            availabilityFrom: availabilityFrom,
            availabilityTo: availabilityTo,
            availabilityEvaluationUtc: evaluationNowUtc,
            skip: validated.Skip,
            take: validated.Take);
        var searchResult = await _assetRepository.SearchPublicAsync(searchCriteria, cancellationToken);
        int totalCount = searchResult.TotalCount;
        int totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling((double)totalCount / validated.Take);

        var mappedItems = searchResult.Items
            .Select(a => PublicInventoryAssetMapper.ToResponse(a, _mediaOptions.PublicGatewayUrl))
            .ToList();

        return Result.Success(new PagedResponse<PublicInventoryAssetResponse>(
            mappedItems,
            request.Page,
            request.PageSize,
            totalCount,
            totalPages));
    }
}
