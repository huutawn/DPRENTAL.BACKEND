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
    private readonly Dprental.InventoryService.Application.Caching.IInventoryReadCache _cache;
    private readonly Dprental.InventoryService.Application.Caching.InventoryCacheOptions _cacheOptions;

    public PublicEquipmentListingService(
        IEquipmentListingRepository listingRepository,
        AssetSearchQueryValidator queryValidator,
        AssetSearchFilterResolver filterResolver,
        IOptions<MediaServiceOptions> mediaOptions,
        TimeProvider? timeProvider = null,
        IInventorySearchIndexService? searchIndexService = null,
        ILogger<PublicEquipmentListingService>? logger = null,
        ICatalogServiceClient? catalogClient = null,
        Locations.IVietnamDivisionsCatalog? divisionsCatalog = null,
        Dprental.InventoryService.Application.Caching.IInventoryReadCache? cache = null,
        IOptions<Dprental.InventoryService.Application.Caching.InventoryCacheOptions>? cacheOptions = null)
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
        _cache = cache ?? Dprental.InventoryService.Application.Caching.NoOpInventoryReadCache.Instance;
        _cacheOptions = cacheOptions?.Value ?? new Dprental.InventoryService.Application.Caching.InventoryCacheOptions();
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

        string scope = $"listing:{id:D}";
        string queryType = "public-detail";
        string inputHash = today.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

        return await _cache.GetOrCreateAsync<PublicEquipmentListingResponse>(
            scope,
            queryType,
            inputHash,
            async (genToken, ct) =>
            {
                var snapshot = await _listingRepository.GetCalendarSnapshotAsync(id, today, today, ct);
                if (snapshot is null || snapshot.Status != EquipmentListingStatus.Active)
                {
                    return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
                }

                var evaluationNowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());

                int totalQuantity = snapshot.Assets.Count(a => a.AcceptingBookings);
                int availableQuantity = snapshot.Assets.Count(a =>
                    a.AcceptingBookings &&
                    a.CalendarBlocks.Count == 0 &&
                    !a.Allocations.Any(al =>
                        al.Status == AllocationStatus.Allocated ||
                        (al.Status == AllocationStatus.Held && al.HoldExpiresAt > evaluationNowUtc)));

                var response = PublicEquipmentListingMapper.ToResponse(
                    snapshot,
                    totalQuantity,
                    availableQuantity,
                    _mediaOptions.PublicGatewayUrl);

                var baseTtl = TimeSpan.FromSeconds(_cacheOptions.DynamicTtlSeconds > 0 ? _cacheOptions.DynamicTtlSeconds : 15);
                DateTimeOffset? nearestHold = snapshot.Assets
                    .SelectMany(a => a.Allocations)
                    .Where(al => al.Status == AllocationStatus.Held && al.HoldExpiresAt.HasValue && al.HoldExpiresAt.Value > evaluationNowUtc)
                    .Select(al => al.HoldExpiresAt)
                    .OrderBy(t => t!.Value)
                    .FirstOrDefault();

                var allAllocations = snapshot.Assets.SelectMany(a => a.Allocations);
                var deadline = Dprental.InventoryService.Application.Caching.CacheKeyHelper.CalculateOccupancyDeadline(
                    evaluationNowUtc.Add(baseTtl),
                    nearestHold,
                    allAllocations,
                    evaluationNowUtc);

                DateTimeOffset? finalDeadline = deadline > evaluationNowUtc ? deadline : null;
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<PublicEquipmentListingResponse>(response, finalDeadline));
            },
            cancellationToken);
    }
    /// <inheritdoc />
    public async Task<Result<EquipmentListingStatisticsResponse>> GetStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        string scope = "public-discovery";
        string queryType = "public-stats";
        string inputHash = "active";

        return await _cache.GetOrCreateAsync<EquipmentListingStatisticsResponse>(
            scope,
            queryType,
            inputHash,
            async (genToken, ct) =>
            {
                var criteria = new EquipmentListingStatisticsCriteria(
                    OwnerUserId: null,
                    Status: EquipmentListingStatus.Active);

                var result = await _listingRepository.GetListingStatisticsAsync(criteria, ct);
                var statsResponse = new EquipmentListingStatisticsResponse(result.ByEquipmentType, result.ByBrand);
                var ttl = _timeProvider.GetUtcNow().AddSeconds(_cacheOptions.MetadataTtlSeconds > 0 ? _cacheOptions.MetadataTtlSeconds : 120);
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<EquipmentListingStatisticsResponse>(statsResponse, ttl));
            },
            cancellationToken);
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
        var structCheck = AssetSearchFilterResolver.ValidateStructure(request.Filters, request.EquipmentTypeId, PublicAssetDataPolicy.IsReservedPropertyName);
        if (structCheck.IsFailure)
        {
            return structCheck.Error;
        }

        var nowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
        var todayVn = BusinessDateTime.ToVietnamDate(nowUtc);

        string searchScope = "public-discovery";
        string searchQueryType = "public-search";
        string searchHash = Dprental.InventoryService.Application.Caching.CacheKeyHelper.ComputePublicSearchHash(
            request.Page,
            request.PageSize,
            request.Q,
            request.EquipmentTypeId,
            request.BrandId,
            request.CategoryId,
            request.ProvinceCode,
            request.WardCode,
            request.Sort,
            request.Filters,
            request.From,
            request.To,
            request.RequestedQuantity,
            todayVn);

        var validated = valResult.Value;

        return await _cache.GetOrCreateAsync<SearchExecution<PagedResponse<PublicEquipmentListingResponse>>>(
            searchScope,
            searchQueryType,
            searchHash,
            async (genToken, ct) =>
            {
                var snapshotStartedAt = _timeProvider.GetUtcNow();
                DateTimeOffset? categoryDeadline = null;
                Guid? effectiveEquipmentTypeId = validated.EquipmentTypeId;
                IReadOnlyList<Guid>? effectiveEquipmentTypeIds = null;

                if (validated.CategoryId.HasValue)
                {
                    if (_catalogClient is null)
                    {
                        return Error.Unavailable("CATALOG_SERVICE_UNAVAILABLE", "CatalogService client chưa được cấu hình.");
                    }

                    var categoryTypeIdsResult = await _catalogClient.GetEquipmentTypeIdsByCategoryAsync(validated.CategoryId.Value, ct);
                    if (categoryTypeIdsResult.IsFailure)
                    {
                        return categoryTypeIdsResult.Error;
                    }

                    var categoryCached = categoryTypeIdsResult.Value;
                    categoryDeadline = categoryCached.ExpiresAtUtc;
                    var categoryTypeIds = categoryCached.Value;

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
                        ct);

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
                    cancellationToken => _listingRepository.GetDistinctSchemaVersionsPublicAsync(
                        new AssetSearchFilters(
                            SearchKeyword: candidateIds == null ? validated.SearchKeyword : null,
                            EquipmentTypeId: effectiveEquipmentTypeId,
                            BrandId: validated.BrandId,
                            CandidateIds: candidateIds,
                            EquipmentTypeIds: effectiveEquipmentTypeIds,
                            ProvinceCode: request.ProvinceCode,
                            WardCode: request.WardCode),
                        cancellationToken),
                    isFieldForbidden: PublicAssetDataPolicy.IsReservedPropertyName,
                    ct);

                if (resolveResult.IsFailure)
                {
                    return resolveResult.Error;
                }

                var resolvedFilters = resolveResult.Value;
                var schemaDeadline = resolvedFilters.ExpiresAtUtc;

                if (effectiveEquipmentTypeIds is not null && effectiveEquipmentTypeIds.Count == 0)
                {
                    var emptyPaged = new PagedResponse<PublicEquipmentListingResponse>(
                        Array.Empty<PublicEquipmentListingResponse>(),
                        request.Page,
                        request.PageSize,
                        0,
                        0);
                    var emptyResult = new SearchExecution<PagedResponse<PublicEquipmentListingResponse>>(emptyPaged, backend);
                    var emptyTtl = _timeProvider.GetUtcNow().AddSeconds(_cacheOptions.DynamicTtlSeconds > 0 ? _cacheOptions.DynamicTtlSeconds : 15);
                    return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<SearchExecution<PagedResponse<PublicEquipmentListingResponse>>>(emptyResult, emptyTtl));
                }

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

                var searchResult = await _listingRepository.SearchPublicAsync(searchCriteria, ct);
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

                var searchExecutionResult = new SearchExecution<PagedResponse<PublicEquipmentListingResponse>>(paged, backend);

                var baseTtl = TimeSpan.FromSeconds(_cacheOptions.DynamicTtlSeconds > 0 ? _cacheOptions.DynamicTtlSeconds : 15);
                var candidateDeadline = snapshotStartedAt.Add(baseTtl);

                if (searchResult.NextAvailabilityChangeAtUtc.HasValue && searchResult.NextAvailabilityChangeAtUtc.Value < candidateDeadline)
                {
                    candidateDeadline = searchResult.NextAvailabilityChangeAtUtc.Value;
                }

                var vnNow = evaluationNowUtc.ToOffset(TimeSpan.FromHours(7));
                var nextVnMidnight = new DateTimeOffset(vnNow.Year, vnNow.Month, vnNow.Day, 0, 0, 0, TimeSpan.FromHours(7)).AddDays(1).ToUniversalTime();
                if (nextVnMidnight < candidateDeadline)
                {
                    candidateDeadline = nextVnMidnight;
                }

                if (schemaDeadline.HasValue && schemaDeadline.Value < candidateDeadline)
                {
                    candidateDeadline = schemaDeadline.Value;
                }

                if (categoryDeadline.HasValue && categoryDeadline.Value < candidateDeadline)
                {
                    candidateDeadline = categoryDeadline.Value;
                }

                DateTimeOffset? finalDeadline = candidateDeadline > evaluationNowUtc ? candidateDeadline : null;
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<SearchExecution<PagedResponse<PublicEquipmentListingResponse>>>(searchExecutionResult, finalDeadline));
            },
            cancellationToken);
    }
}
