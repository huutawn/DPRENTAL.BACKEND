using Dprental.InventoryService.Application.DTOs.Listings;
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
    private readonly TimeProvider _timeProvider;
    private readonly IInventorySearchIndexService? _searchIndexService;
    private readonly ILogger<PublicEquipmentListingService>? _logger;
    public PublicEquipmentListingService(
        IEquipmentListingRepository listingRepository,
        AssetSearchQueryValidator queryValidator,
        AssetSearchFilterResolver filterResolver,
        IOptions<MediaServiceOptions> mediaOptions,
        TimeProvider? timeProvider = null,
        IInventorySearchIndexService? searchIndexService = null,
        ILogger<PublicEquipmentListingService>? logger = null)
    {
        _listingRepository = listingRepository ?? throw new ArgumentNullException(nameof(listingRepository));
        _queryValidator = queryValidator ?? throw new ArgumentNullException(nameof(queryValidator));
        _filterResolver = filterResolver ?? throw new ArgumentNullException(nameof(filterResolver));
        ArgumentNullException.ThrowIfNull(mediaOptions);
        _mediaOptions = mediaOptions.Value ?? throw new ArgumentException("MediaServiceOptions không được null.", nameof(mediaOptions));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _searchIndexService = searchIndexService;
        _logger = logger;
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
                    EquipmentTypeId: validated.EquipmentTypeId,
                    BrandId: validated.BrandId),
                cancellationToken);

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
            validated.EquipmentTypeId,
            ct => _listingRepository.GetDistinctSchemaVersionsPublicAsync(
                new AssetSearchFilters(
                    SearchKeyword: candidateIds == null ? validated.SearchKeyword : null,
                    EquipmentTypeId: validated.EquipmentTypeId,
                    BrandId: validated.BrandId,
                    CandidateIds: candidateIds),
                ct),
            isFieldForbidden: PublicAssetDataPolicy.IsReservedPropertyName,
            cancellationToken);

        if (resolveResult.IsFailure)
        {
            return resolveResult.Error;
        }

        var resolvedFilters = resolveResult.Value;
        var evaluationNowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());

        var searchFilters = new AssetSearchFilters(
            SearchKeyword: candidateIds == null ? validated.SearchKeyword : null,
            EquipmentTypeId: validated.EquipmentTypeId,
            BrandId: validated.BrandId,
            Sort: validated.Sort,
            CompatibleSchemaVersions: resolvedFilters.CompatibleSchemaVersions,
            DynamicFilters: resolvedFilters.Criteria,
            CandidateIds: candidateIds);
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
