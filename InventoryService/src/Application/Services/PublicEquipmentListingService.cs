using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Options;
using Dprental.InventoryService.Application.Search;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Application.Services;

/// <summary>
/// Dịch vụ ứng dụng tìm kiếm, lọc và đọc chi tiết EquipmentListing công khai (Public Equipment Listing Discovery).
/// </summary>
public sealed class PublicEquipmentListingService : IPublicEquipmentListingService
{
    private readonly IEquipmentListingRepository _listingRepository;
    private readonly AssetSearchQueryValidator _queryValidator;
    private readonly AssetSearchFilterResolver _filterResolver;
    private readonly MediaServiceOptions _mediaOptions;
    private readonly ICatalogServiceClient? _catalogClient;
    private readonly TimeProvider _timeProvider;
    private readonly IInventorySearchIndexService? _searchIndexService;
    private readonly ILogger<PublicEquipmentListingService>? _logger;
    private readonly Locations.IVietnamDivisionsCatalog _divisionsCatalog;
    public PublicEquipmentListingService(
        IEquipmentListingRepository listingRepository,
        AssetSearchQueryValidator queryValidator,
        AssetSearchFilterResolver filterResolver,
        IOptions<MediaServiceOptions> mediaOptions,
        TimeProvider? timeProvider = null,
        IInventorySearchIndexService? searchIndexService = null,
        ILogger<PublicEquipmentListingService>? logger = null,
        ICatalogServiceClient? catalogClient = null,
        Locations.IVietnamDivisionsCatalog? divisionsCatalog = null)
    {
        _listingRepository = listingRepository ?? throw new ArgumentNullException(nameof(listingRepository));
        _queryValidator = queryValidator ?? throw new ArgumentNullException(nameof(queryValidator));
        _filterResolver = filterResolver ?? throw new ArgumentNullException(nameof(filterResolver));
        ArgumentNullException.ThrowIfNull(mediaOptions);
        _mediaOptions = mediaOptions.Value ?? throw new ArgumentException("MediaServiceOptions không được null.", nameof(mediaOptions));
        _catalogClient = catalogClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _searchIndexService = searchIndexService;
        _logger = logger;
        _divisionsCatalog = divisionsCatalog ?? new Locations.VietnamDivisionsCatalog();
    }

    /// <inheritdoc />
    public async Task<Result<PublicEquipmentListingResponse>> GetPublicListingByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        var nowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
        var today = BusinessDateTime.ToVietnamDate(nowUtc);

        var snapshot = await _listingRepository.GetCalendarSnapshotAsync(id, today, today, cancellationToken);
        if (snapshot is null || snapshot.Status != EquipmentListingStatus.Active)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        int totalQuantity = snapshot.Assets.Count(a => a.AcceptingBookings);
        int availableQuantity = snapshot.Assets.Count(a =>
            a.AcceptingBookings &&
            a.CalendarBlocks.Count == 0 &&
            !a.Allocations.Any(al =>
                al.Status == AllocationStatus.Allocated ||
                (al.Status == AllocationStatus.Held && al.HoldExpiresAt > nowUtc)));

        return Result.Success(PublicEquipmentListingMapper.ToResponse(
            snapshot,
            totalQuantity,
            availableQuantity,
            _mediaOptions.PublicGatewayUrl));
    }

    /// <inheritdoc />
    public async Task<Result<EquipmentListingStatisticsResponse>> GetStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        var criteria = new EquipmentListingStatisticsCriteria(
            OwnerUserId: null,
            Status: EquipmentListingStatus.Active);

        var result = await _listingRepository.GetListingStatisticsAsync(criteria, cancellationToken);
        return Result.Success(new EquipmentListingStatisticsResponse(result.ByEquipmentType, result.ByBrand));
    }

    /// <inheritdoc />
    public async Task<Result<SearchExecution<PagedResponse<PublicEquipmentListingResponse>>>> ListPublicListingsAsync(
        ListPublicEquipmentListingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var valResult = AssetSearchQueryValidator.ValidateListingQuery(
            request.Page,
            request.PageSize,
            request.Q,
            request.EquipmentTypeId,
            request.BrandId,
            request.Sort,
            request.CategoryId);

        if (valResult.IsFailure)
        {
            return valResult.Error;
        }

        if (!string.IsNullOrWhiteSpace(request.WardCode))
        {
            if (string.IsNullOrWhiteSpace(request.ProvinceCode))
            {
                return Error.Validation("INVALID_LOCATION", "Lọc theo phường/xã yêu cầu phải cung cấp mã tỉnh/thành phố tương ứng.");
            }

            if (!_divisionsCatalog.IsValidPair(request.ProvinceCode, request.WardCode, out _, out _))
            {
                return Error.Validation("INVALID_LOCATION", "Cặp tỉnh/thành phố và phường/xã không hợp lệ.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.ProvinceCode))
        {
            if (!_divisionsCatalog.IsValidProvince(request.ProvinceCode, out _))
            {
                return Error.Validation("INVALID_LOCATION", "Mã tỉnh/thành phố không hợp lệ.");
            }
        }

        var validated = valResult.Value;

        Guid? effectiveEquipmentTypeId = validated.EquipmentTypeId;
        IReadOnlyList<Guid>? effectiveEquipmentTypeIds = null;

        if (validated.CategoryId.HasValue)
        {
            if (_catalogClient is null)
            {
                return Error.Unavailable("CATALOG_SERVICE_UNAVAILABLE", "CatalogService client chưa được cấu hình.");
            }

            var categoryTypeIdsResult = await _catalogClient.GetEquipmentTypeIdsByCategoryAsync(validated.CategoryId.Value, cancellationToken);
            if (categoryTypeIdsResult.IsFailure)
            {
                return categoryTypeIdsResult.Error;
            }

            var categoryTypeIds = categoryTypeIdsResult.Value;

            if (validated.EquipmentTypeId.HasValue)
            {
                if (categoryTypeIds.Contains(validated.EquipmentTypeId.Value))
                {
                    effectiveEquipmentTypeId = validated.EquipmentTypeId.Value;
                    effectiveEquipmentTypeIds = null;
                }
                else
                {
                    effectiveEquipmentTypeId = null;
                    effectiveEquipmentTypeIds = categoryTypeIds;
                }
            }
            else
            {
                effectiveEquipmentTypeId = null;
                effectiveEquipmentTypeIds = categoryTypeIds;
            }

        }

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

        if (request.RequestedQuantity.HasValue)
        {
            if (!availabilityFrom.HasValue || !availabilityTo.HasValue)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", "Truyền requestedQuantity phải kèm cả hai tham số ngày 'from' và 'to'.");
            }

            if (request.RequestedQuantity.Value < 1)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", "RequestedQuantity phải lớn hơn hoặc bằng 1.");
            }
        }

        IReadOnlyList<Guid>? candidateIds = null;
        string backend = "postgres";

        if (!string.IsNullOrWhiteSpace(validated.SearchKeyword) && _searchIndexService != null)
        {
            var candidateResult = await _searchIndexService.SearchPublicListingCandidatesAsync(
                new PublicListingSearchIndexQuery(
                    Keyword: validated.SearchKeyword,
                    EquipmentTypeId: effectiveEquipmentTypeId,
                    BrandId: validated.BrandId),
                cancellationToken);

            if (candidateResult.IsFailure && candidateResult.Error.Code is
                "SEARCH_CREDENTIALS_INVALID" or "SEARCH_CONFIGURATION_INVALID")
                return candidateResult.Error;

            if (candidateResult.IsSuccess && !candidateResult.Value.IsTruncated)
            {
                candidateIds = candidateResult.Value.CandidateIds;
                backend = "meilisearch";
            }
            else
            {
                _logger?.LogWarning(
                    "Meilisearch query failed or candidates truncated for query '{Keyword}'. Falling back to PostgreSQL. Reason: {Error}",
                    validated.SearchKeyword,
                    candidateResult.IsFailure ? candidateResult.Error.Message : "Truncated (exceeded candidate limit)");
                candidateIds = null;
                backend = "postgres";
            }
        }


        var resolveResult = await _filterResolver.ResolveAsync(
            request.Filters,
            effectiveEquipmentTypeId,
            ct => _listingRepository.GetDistinctSchemaVersionsPublicAsync(
                new AssetSearchFilters(
                    SearchKeyword: candidateIds == null ? validated.SearchKeyword : null,
                    EquipmentTypeId: effectiveEquipmentTypeId,
                    BrandId: validated.BrandId,
                    CandidateIds: candidateIds,
                    EquipmentTypeIds: effectiveEquipmentTypeIds,
                    ProvinceCode: request.ProvinceCode,
                    WardCode: request.WardCode),
                ct),
            isFieldForbidden: PublicAssetDataPolicy.IsReservedPropertyName,
            cancellationToken);

        if (resolveResult.IsFailure)
        {
            return resolveResult.Error;
        }

        if (effectiveEquipmentTypeIds is not null && effectiveEquipmentTypeIds.Count == 0)
        {
            var emptyPaged = new PagedResponse<PublicEquipmentListingResponse>(
                Array.Empty<PublicEquipmentListingResponse>(),
                request.Page,
                request.PageSize,
                0,
                0);
            return Result.Success(new SearchExecution<PagedResponse<PublicEquipmentListingResponse>>(emptyPaged, backend));
        }

        var resolvedFilters = resolveResult.Value;
        var evaluationNowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());

        var searchFilters = new AssetSearchFilters(
            SearchKeyword: candidateIds == null ? validated.SearchKeyword : null,
            EquipmentTypeId: effectiveEquipmentTypeId,
            BrandId: validated.BrandId,
            Sort: validated.Sort,
            CompatibleSchemaVersions: resolvedFilters.CompatibleSchemaVersions,
            DynamicFilters: resolvedFilters.Criteria,
            CandidateIds: candidateIds,
            EquipmentTypeIds: effectiveEquipmentTypeIds,
            ProvinceCode: request.ProvinceCode,
            WardCode: request.WardCode);
        var searchCriteria = new EquipmentListingSearchCriteria(
            filters: searchFilters,
            availabilityFrom: availabilityFrom,
            availabilityTo: availabilityTo,
            requestedQuantity: request.RequestedQuantity,
            availabilityEvaluationUtc: evaluationNowUtc,
            skip: validated.Skip,
            take: validated.Take);

        var searchResult = await _listingRepository.SearchPublicAsync(searchCriteria, cancellationToken);
        int totalCount = searchResult.TotalCount;
        int totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling((double)totalCount / validated.Take);

        var mappedItems = searchResult.Items
            .Select(item => PublicEquipmentListingMapper.ToResponse(
                item.Listing,
                item.TotalQuantity,
                item.AvailableQuantity,
                _mediaOptions.PublicGatewayUrl))
            .ToList();

        var paged = new PagedResponse<PublicEquipmentListingResponse>(
            mappedItems,
            request.Page,
            request.PageSize,
            totalCount,
            totalPages);

        return Result.Success(new SearchExecution<PagedResponse<PublicEquipmentListingResponse>>(paged, backend));
    }
}
