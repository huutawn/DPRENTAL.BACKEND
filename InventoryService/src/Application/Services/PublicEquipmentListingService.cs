using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Recommendation;
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
public sealed partial class PublicEquipmentListingService : IPublicEquipmentListingService
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
    private readonly IRecommendationRankingReader? _recommendationReader;
    private readonly IRecommendationMetrics? _recommendationMetrics;
    private readonly IBehaviorEventRecorder? _behaviorRecorder;

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
        IOptions<Dprental.InventoryService.Application.Caching.InventoryCacheOptions>? cacheOptions = null,
        IRecommendationRankingReader? recommendationReader = null,
        IRecommendationMetrics? recommendationMetrics = null,
        IBehaviorEventRecorder? behaviorRecorder = null)
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
        _recommendationReader = recommendationReader;
        _recommendationMetrics = recommendationMetrics;
        _behaviorRecorder = behaviorRecorder;
    }

    private sealed record CoreSearchResult(
        Dprental.InventoryService.Application.Caching.CachedRead<SearchExecution<PagedResponse<PublicEquipmentListingResponse>>> Cached,
        string? EffectiveTier);

    /// <inheritdoc />
    public async Task<Result<SearchExecution<PagedResponse<PublicEquipmentListingResponse>>>> ListPublicListingsAsync(
        ListPublicEquipmentListingsRequest request,
        IInventoryCallerContext callerContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callerContext);
        cancellationToken.ThrowIfCancellationRequested();

        var valResult = ValidatePublicListingRequest(request);
        if (valResult.IsFailure)
        {
            return valResult.Error;
        }

        var validated = valResult.Value;
        bool isRecommendation = validated.Sort == AssetSortOption.Recommended;

        // Recommended dùng cùng bộ lọc và truy vấn PostgreSQL với Search, chỉ thay cách xếp hạng.
        // Điểm Valkey là tín hiệu ưu tiên, không phải danh sách giới hạn những listing được phép trả về.
        if (isRecommendation)
        {
            // Chỉ cá nhân hóa cho người dùng thật có danh tính hợp lệ; anonymous/service dùng độ phổ biến chung.
            // Đồng thời loại listing của chính người dùng để tránh gợi ý họ thuê thiết bị của mình.
            Guid? effectiveUserId = (callerContext.IsHuman && callerContext.UserId.HasValue && callerContext.UserId.Value != Guid.Empty)
                ? callerContext.UserId.Value
                : null;

            Guid? excludedOwnerUserId = effectiveUserId;

            RecommendationSnapshot snapshot;
            if (_recommendationReader != null)
            {
                snapshot = await _recommendationReader.ReadAsync(effectiveUserId, cancellationToken);
            }
            else
            {
                snapshot = RecommendationSnapshot.Unavailable(RecommendationReasons.Unconfigured);
            }

            // Không đưa kết quả cá nhân hóa vào cache Search dùng chung giữa các người dùng.
            var coreResult = await ExecuteSearchCoreAsync(
                request,
                validated,
                snapshot,
                excludedOwnerUserId,
                cancellationToken);

            if (coreResult.IsFailure)
            {
                return coreResult.Error;
            }

            // Tier thực tế được xác định sau khi lọc trong SQL: điểm có thể tồn tại nhưng không còn
            // listing phù hợp. Phân biệt tình huống này (filtered) với lỗi hoặc thiếu dữ liệu Valkey.
            string tier = coreResult.Value.EffectiveTier ?? "default";
            string reason;
            if (snapshot.Outcome != RecommendationOutcome.Available)
            {
                reason = snapshot.Reason;
            }
            else
            {
                if (tier == "personalized")
                {
                    reason = RecommendationReasons.Available;
                }
                else if (tier == "global")
                {
                    reason = effectiveUserId.HasValue ? RecommendationReasons.Filtered : RecommendationReasons.Available;
                }
                else
                {
                    reason = RecommendationReasons.Filtered;
                }
            }

            _recommendationMetrics?.RecordRequest(tier, reason);
            var recResult = Result.Success(coreResult.Value.Cached.Value);
            if (recResult.IsSuccess && _behaviorRecorder != null)
            {
                await _behaviorRecorder.RecordSearchPerformedAsync(
                    request,
                    recResult.Value.Value.TotalCount,
                    callerContext,
                    cancellationToken);
            }
            return recResult;
        }
        else
        {
            var nowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
            var todayVn = BusinessDateTime.ToVietnamDate(nowUtc);

            string searchScope = "public-discovery";
            string searchQueryType = "public-search";
            string effectiveSortStr = validated.Sort == AssetSortOption.Relevance ? "relevance" : (request.Sort ?? "created_desc");
            // Khóa cache bao gồm bộ lọc, phân trang và ngày Việt Nam vì số lượng trống phụ thuộc ngày.
            string searchHash = Dprental.InventoryService.Application.Caching.CacheKeyHelper.ComputePublicSearchHash(
                request.Page,
                request.PageSize,
                request.Q,
                request.EquipmentTypeId,
                request.BrandId,
                request.CategoryId,
                request.ProvinceCode,
                request.WardCode,
                effectiveSortStr,
                request.Filters,
                request.From,
                request.To,
                request.RequestedQuantity,
                todayVn);

            var searchResult = await _cache.GetOrCreateAsync<SearchExecution<PagedResponse<PublicEquipmentListingResponse>>>(
                searchScope,
                searchQueryType,
                searchHash,
                async (genToken, ct) =>
                {
                    var coreResult = await ExecuteSearchCoreAsync(
                        request,
                        validated,
                        recommendationSnapshot: null,
                        excludedOwnerUserId: null,
                        ct);

                    if (coreResult.IsFailure)
                    {
                        return coreResult.Error;
                    }

                    return Result.Success(coreResult.Value.Cached);
                },
                cancellationToken);

            if (searchResult.IsSuccess && _behaviorRecorder != null)
            {
                await _behaviorRecorder.RecordSearchPerformedAsync(
                    request,
                    searchResult.Value.Value.TotalCount,
                    callerContext,
                    cancellationToken);
            }

            return searchResult;
        }
    }

    private Result<ValidatedAssetSearchQuery> ValidatePublicListingRequest(ListPublicEquipmentListingsRequest request)
    {
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

        return valResult;
    }

    private async Task<Result<(Guid? EffectiveTypeId, IReadOnlyList<Guid>? EffectiveTypeIds, DateTimeOffset? Deadline)>> ResolveCategoryTypesAsync(
        Guid? categoryId,
        Guid? equipmentTypeId,
        CancellationToken ct)
    {
        if (!categoryId.HasValue)
        {
            return Result.Success<(Guid?, IReadOnlyList<Guid>?, DateTimeOffset?)>((equipmentTypeId, null, null));
        }

        if (_catalogClient is null)
        {
            return Error.Unavailable("CATALOG_SERVICE_UNAVAILABLE", "CatalogService client chưa được cấu hình.");
        }

        var categoryTypeIdsResult = await _catalogClient.GetEquipmentTypeIdsByCategoryAsync(categoryId.Value, ct);
        if (categoryTypeIdsResult.IsFailure)
        {
            return categoryTypeIdsResult.Error;
        }

        var categoryCached = categoryTypeIdsResult.Value;
        var categoryTypeIds = categoryCached.Value;
        DateTimeOffset? categoryDeadline = categoryCached.ExpiresAtUtc;

        if (equipmentTypeId.HasValue)
        {
            if (categoryTypeIds.Contains(equipmentTypeId.Value))
            {
                return Result.Success<(Guid?, IReadOnlyList<Guid>?, DateTimeOffset?)>((equipmentTypeId.Value, null, categoryDeadline));
            }
            else
            {
                return Result.Success<(Guid?, IReadOnlyList<Guid>?, DateTimeOffset?)>((null, categoryTypeIds, categoryDeadline));
            }
        }

        return Result.Success<(Guid?, IReadOnlyList<Guid>?, DateTimeOffset?)>((null, categoryTypeIds, categoryDeadline));
    }

    private static Result<(DateOnly? From, DateOnly? To)> ResolveDateWindow(
        DateOnly? requestFrom,
        DateOnly? requestTo,
        int? requestedQuantity)
    {
        DateOnly? availabilityFrom = null;
        DateOnly? availabilityTo = null;

        if (requestFrom.HasValue || requestTo.HasValue)
        {
            var dateValidationResult = AvailabilityDateValidator.ValidateDateWindow(
                requestFrom,
                requestTo,
                out var validStart,
                out var validEnd);

            if (dateValidationResult.IsFailure)
            {
                return dateValidationResult.Error;
            }

            availabilityFrom = validStart;
            availabilityTo = validEnd;
        }

        if (requestedQuantity.HasValue)
        {
            if (!availabilityFrom.HasValue || !availabilityTo.HasValue)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", "Truyền requestedQuantity phải kèm cả hai tham số ngày 'from' và 'to'.");
            }

            if (requestedQuantity.Value < 1)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", "RequestedQuantity phải lớn hơn hoặc bằng 1.");
            }
        }

        return Result.Success<(DateOnly?, DateOnly?)>((availabilityFrom, availabilityTo));
    }

    private async Task<Result<(IReadOnlyList<Guid>? CandidateIds, string Backend)>> ResolveCandidatesAsync(
        ValidatedAssetSearchQuery validated,
        Guid? effectiveEquipmentTypeId,
        CancellationToken ct)
    {
        // Chỉ mục chỉ cung cấp ID ứng viên cho Search có từ khóa. Recommended bỏ qua chỉ mục
        // để SQL tự lọc toàn bộ tập hợp hợp lệ trước khi áp dụng điểm và phân trang.
        if (validated.Sort != AssetSortOption.Recommended &&
            !string.IsNullOrWhiteSpace(validated.SearchKeyword) &&
            _searchIndexService != null)
        {
            var candidateResult = await _searchIndexService.SearchPublicListingCandidatesAsync(
                new PublicListingSearchIndexQuery(
                    Keyword: validated.SearchKeyword,
                    EquipmentTypeId: effectiveEquipmentTypeId,
                    BrandId: validated.BrandId),
                ct);

            // Sai quyền/cấu hình cần trả lỗi rõ ràng. Lỗi tạm thời, chưa sẵn sàng hoặc tập ứng viên
            // bị cắt thì dùng SQL để tránh làm mất kết quả hoặc tính sai tổng số trang.
            if (candidateResult.IsFailure && candidateResult.Error.Code is
                "SEARCH_CREDENTIALS_INVALID" or "SEARCH_CONFIGURATION_INVALID")
            {
                return candidateResult.Error;
            }

            if (candidateResult.IsSuccess && !candidateResult.Value.IsTruncated)
            {
                return Result.Success<(IReadOnlyList<Guid>?, string)>((candidateResult.Value.CandidateIds, "meilisearch"));
            }

            _logger?.LogWarning(
                "Meilisearch query failed or candidates truncated for query '{Keyword}'. Falling back to PostgreSQL. Reason: {Error}",
                validated.SearchKeyword,
                candidateResult.IsFailure ? candidateResult.Error.Message : "Truncated (exceeded candidate limit)");
        }

        return Result.Success<(IReadOnlyList<Guid>?, string)>((null, "postgres"));
    }

    private async Task<Result<CoreSearchResult>> ExecuteSearchCoreAsync(
        ListPublicEquipmentListingsRequest request,
        ValidatedAssetSearchQuery validated,
        RecommendationSnapshot? recommendationSnapshot,
        Guid? excludedOwnerUserId,
        CancellationToken ct)
    {
        var snapshotStartedAt = _timeProvider.GetUtcNow();

        var categoryResult = await ResolveCategoryTypesAsync(validated.CategoryId, validated.EquipmentTypeId, ct);
        if (categoryResult.IsFailure)
        {
            return categoryResult.Error;
        }

        var (effectiveEquipmentTypeId, effectiveEquipmentTypeIds, categoryDeadline) = categoryResult.Value;

        var dateResult = ResolveDateWindow(request.From, request.To, request.RequestedQuantity);
        if (dateResult.IsFailure)
        {
            return dateResult.Error;
        }

        var (availabilityFrom, availabilityTo) = dateResult.Value;

        var candidatesResult = await ResolveCandidatesAsync(validated, effectiveEquipmentTypeId, ct);
        if (candidatesResult.IsFailure)
        {
            return candidatesResult.Error;
        }

        var (candidateIds, backend) = candidatesResult.Value;

        // Kiểm tra bộ lọc thông số theo schema Catalog và chặn trường riêng tư ở luồng công khai.
        var resolveResult = await _filterResolver.ResolveAsync(
            request.Filters,
            effectiveEquipmentTypeId,
            cancellationToken => _listingRepository.GetDistinctSchemaVersionsPublicAsync(
                new AssetSearchFilters(
                    SearchKeyword: validated.SearchKeyword,
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
            var emptyCached = new Dprental.InventoryService.Application.Caching.CachedRead<SearchExecution<PagedResponse<PublicEquipmentListingResponse>>>(emptyResult, emptyTtl);
            return Result.Success(new CoreSearchResult(emptyCached, "default"));
        }

        var evaluationNowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());

        var searchFilters = new AssetSearchFilters(
            SearchKeyword: validated.SearchKeyword,
            EquipmentTypeId: effectiveEquipmentTypeId,
            BrandId: validated.BrandId,
            Sort: validated.Sort,
            CompatibleSchemaVersions: resolvedFilters.CompatibleSchemaVersions,
            DynamicFilters: resolvedFilters.Criteria,
            CandidateIds: candidateIds,
            EquipmentTypeIds: effectiveEquipmentTypeIds,
            ProvinceCode: request.ProvinceCode,
            WardCode: request.WardCode);

        var personalScores = recommendationSnapshot?.Outcome == RecommendationOutcome.Available
            ? recommendationSnapshot.PersonalInterests
            : null;
        var globalScores = recommendationSnapshot?.Outcome == RecommendationOutcome.Available
            ? recommendationSnapshot.GlobalPopularity
            : null;

        var searchCriteria = new EquipmentListingSearchCriteria(
            filters: searchFilters,
            availabilityFrom: availabilityFrom,
            availabilityTo: availabilityTo,
            requestedQuantity: request.RequestedQuantity,
            availabilityEvaluationUtc: evaluationNowUtc,
            skip: validated.Skip,
            take: validated.Take,
            personalScores: personalScores,
            globalScores: globalScores,
            excludedOwnerUserId: excludedOwnerUserId,
            seed: recommendationSnapshot?.Seed);

        // PostgreSQL kiểm tra trạng thái, vị trí, thông số và khả dụng từ dữ liệu nguồn;
        // ID ứng viên từ Meilisearch không thay thế các kiểm tra này vì chỉ mục có thể cập nhật trễ.
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

        // Cache hết hạn theo mốc sớm nhất: TTL, hold sắp hết hạn, nửa đêm Việt Nam,
        // schema hoặc danh mục. Nhờ đó không giữ kết quả lâu hơn dữ liệu đã dùng để tính nó.
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
        var cachedRead = new Dprental.InventoryService.Application.Caching.CachedRead<SearchExecution<PagedResponse<PublicEquipmentListingResponse>>>(searchExecutionResult, finalDeadline);

        return Result.Success(new CoreSearchResult(cachedRead, searchResult.EffectiveTier));
    }
}
