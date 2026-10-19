using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Options;
using Dprental.InventoryService.Application.Search;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Application.Services;

/// <summary>
/// Dịch vụ ứng dụng quản lý danh mục niêm yết thiết bị (EquipmentListing) và các đơn vị thiết bị vật lý.
/// </summary>
public sealed partial class EquipmentListingService : IEquipmentListingService
{
    private readonly IEquipmentListingRepository _listingRepository;
    private readonly IInventoryAssetRepository _assetRepository;
    private readonly IInventoryWriteTransaction _writeTransaction;
    private readonly ICatalogServiceClient _catalogClient;
    private readonly IInventoryMediaService _mediaService;
    private readonly MediaServiceOptions _mediaOptions;
    private readonly TimeProvider _timeProvider;
    private readonly IInventorySearchIndexService? _searchIndexService;
    private readonly ILogger<EquipmentListingService>? _logger;
    private readonly Dprental.InventoryService.Application.Caching.IInventoryReadCache _cache;
    private readonly Dprental.InventoryService.Application.Caching.InventoryCacheOptions _cacheOptions;

    public EquipmentListingService(
        IEquipmentListingRepository listingRepository,
        IInventoryAssetRepository assetRepository,
        IInventoryWriteTransaction writeTransaction,
        ICatalogServiceClient catalogClient,
        IInventoryMediaService mediaService,
        IOptions<MediaServiceOptions> mediaOptions,
        TimeProvider? timeProvider = null,
        IInventorySearchIndexService? searchIndexService = null,
        ILogger<EquipmentListingService>? logger = null)
        : this(listingRepository, assetRepository, writeTransaction, catalogClient, mediaService, mediaOptions, null, null, timeProvider, searchIndexService, logger)
    {
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public EquipmentListingService(
        IEquipmentListingRepository listingRepository,
        IInventoryAssetRepository assetRepository,
        IInventoryWriteTransaction writeTransaction,
        ICatalogServiceClient catalogClient,
        IInventoryMediaService mediaService,
        IOptions<MediaServiceOptions> mediaOptions,
        Dprental.InventoryService.Application.Caching.IInventoryReadCache? cache = null,
        IOptions<Dprental.InventoryService.Application.Caching.InventoryCacheOptions>? cacheOptions = null,
        TimeProvider? timeProvider = null,
        IInventorySearchIndexService? searchIndexService = null,
        ILogger<EquipmentListingService>? logger = null)
    {
        _listingRepository = listingRepository ?? throw new ArgumentNullException(nameof(listingRepository));
        _assetRepository = assetRepository ?? throw new ArgumentNullException(nameof(assetRepository));
        _writeTransaction = writeTransaction ?? throw new ArgumentNullException(nameof(writeTransaction));
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _mediaService = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
        ArgumentNullException.ThrowIfNull(mediaOptions);
        _mediaOptions = mediaOptions.Value ?? throw new ArgumentException("MediaServiceOptions không được null.", nameof(mediaOptions));
        _cache = cache ?? Dprental.InventoryService.Application.Caching.NoOpInventoryReadCache.Instance;
        _cacheOptions = cacheOptions?.Value ?? new Dprental.InventoryService.Application.Caching.InventoryCacheOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _searchIndexService = searchIndexService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<EquipmentListingResponse>> GetListingByIdAsync(
        Guid id,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (caller.IsService || !caller.UserId.HasValue)
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ người dùng cá nhân mới có quyền truy cập Listing.");
        }

        string scope = $"listing:{id:D}";
        string queryType = "owner-detail";
        string inputHash = "detail";

        var pipelineResult = await _cache.GetOrCreateAsync<EquipmentListingResponse>(
            scope,
            queryType,
            inputHash,
            async (genToken, ct) =>
            {
                var listing = await _listingRepository.GetByIdAsync(id, ct);
                if (listing is null)
                {
                    return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
                }

                int totalQuantity = await _listingRepository.GetTotalQuantityAsync(id, ct);
                var response = EquipmentListingMapper.ToResponse(listing, totalQuantity, _mediaOptions.PublicGatewayUrl);
                var ttl = _timeProvider.GetUtcNow().AddSeconds(_cacheOptions.MetadataTtlSeconds > 0 ? _cacheOptions.MetadataTtlSeconds : 120);
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<EquipmentListingResponse>(response, ttl));
            },
            cancellationToken);

        if (pipelineResult.IsFailure)
        {
            return pipelineResult.Error;
        }

        var listingResponse = pipelineResult.Value;
        if (!InventoryAccessEvaluator.CanViewOrEditListing(caller, listingResponse.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền truy cập Listing của người dùng khác.");
        }

        return listingResponse;
    }
    /// <inheritdoc />
    public async Task<Result<SearchExecution<PagedResponse<EquipmentListingResponse>>>> ListOwnerListingsAsync(
        ListEquipmentListingsRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (caller.IsService || !caller.UserId.HasValue)
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ người dùng cá nhân mới có danh sách Listing sở hữu.");
        }
        var keywordResult = AssetSearchQueryValidator.ValidateSearchKeyword(request.Q);
        if (keywordResult.IsFailure)
        {
            return keywordResult.Error;
        }

        if (request.EquipmentTypeId.HasValue && request.EquipmentTypeId.Value == Guid.Empty)
        {
            return Error.Validation("INVALID_QUERY_PARAMETER", "EquipmentTypeId không được là Guid rỗng.");
        }

        if (request.BrandId.HasValue && request.BrandId.Value == Guid.Empty)
        {
            return Error.Validation("INVALID_QUERY_PARAMETER", "BrandId không được là Guid rỗng.");
        }

        if (request.Status.HasValue && !Enum.IsDefined(request.Status.Value))
        {
            return Error.Validation("INVALID_QUERY_PARAMETER", "Trạng thái không hợp lệ.");
        }
        Guid ownerUserId = caller.UserId.Value;
        string searchScope = $"owner:{ownerUserId:D}";
        string searchQueryType = "owner-listings";
        string searchHash = Dprental.InventoryService.Application.Caching.CacheKeyHelper.ComputeOwnerListingSearchHash(
            request.Page,
            request.PageSize,
            request.Q,
            request.EquipmentTypeId,
            request.BrandId,
            request.Status,
            request.Sort);

        int page = Math.Max(1, request.Page);
        int pageSize = Math.Clamp(request.PageSize, 1, 100);
        long offset = (long)(page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            return Error.Validation("INVALID_PAGE", "Chỉ số trang vượt quá giới hạn hệ thống.");
        }
        int skip = (int)offset;

        AssetSortOption sortOption = !string.IsNullOrWhiteSpace(keywordResult.Value)
            ? AssetSortOption.Relevance
            : AssetSortOption.CreatedDesc;

        if (!string.IsNullOrWhiteSpace(request.Sort))
        {
            string sortStr = request.Sort.Trim().ToLowerInvariant();
            sortOption = sortStr switch
            {
                "created_desc" => AssetSortOption.CreatedDesc,
                "created_asc" => AssetSortOption.CreatedAsc,
                "model_desc" => AssetSortOption.ModelDesc,
                "model_asc" => AssetSortOption.ModelAsc,
                "relevance" => AssetSortOption.Relevance,
                _ => (AssetSortOption)(-1)
            };

            if ((int)sortOption == -1)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", "Tùy chọn sắp xếp (sort) không hợp lệ.");
            }
        }

        return await _cache.GetOrCreateAsync<SearchExecution<PagedResponse<EquipmentListingResponse>>>(
            searchScope,
            searchQueryType,
            searchHash,
            async (genToken, ct) =>
            {
                IReadOnlyList<Guid>? candidateIds = null;
                string backend = "postgres";

                if (!string.IsNullOrWhiteSpace(keywordResult.Value) && _searchIndexService != null)
                {
                    var query = new OwnerListingSearchIndexQuery(
                        ownerUserId,
                        keywordResult.Value,
                        request.EquipmentTypeId,
                        request.BrandId,
                        request.Status.HasValue ? (int)request.Status.Value : null);

                    var candidateResult = await _searchIndexService.SearchOwnerListingCandidatesAsync(query, ct);
                    if (candidateResult.IsFailure && candidateResult.Error.Code is "SEARCH_CREDENTIALS_INVALID" or "SEARCH_CONFIGURATION_INVALID")
                    {
                        return candidateResult.Error;
                    }

                    if (candidateResult.IsSuccess && !candidateResult.Value.IsTruncated)
                    {
                        candidateIds = candidateResult.Value.CandidateIds;
                        backend = "meilisearch";
                    }
                    else
                    {
                        _logger?.LogWarning(
                            "Meilisearch query failed or candidates truncated for owner '{OwnerUserId}', keyword '{Keyword}'. Falling back to PostgreSQL.",
                            ownerUserId,
                            keywordResult.Value);
                        candidateIds = null;
                        backend = "postgres";
                    }
                }

                var criteria = new OwnerEquipmentListingSearchCriteria(
                    ownerUserId,
                    candidateIds == null ? keywordResult.Value : null,
                    skip,
                    pageSize,
                    request.EquipmentTypeId,
                    request.BrandId,
                    request.Status,
                    candidateIds,
                    sortOption);
                var searchResult = await _listingRepository.SearchOwnerAsync(criteria, ct);
                int totalPages = searchResult.TotalCount == 0 ? 0 : (int)Math.Ceiling((double)searchResult.TotalCount / pageSize);

                var items = searchResult.Items
                    .Select(item => EquipmentListingMapper.ToResponse(item.Listing, item.TotalQuantity, _mediaOptions.PublicGatewayUrl))
                    .ToList();

                var paged = new PagedResponse<EquipmentListingResponse>(items, page, pageSize, searchResult.TotalCount, totalPages);
                var searchExecutionResult = new SearchExecution<PagedResponse<EquipmentListingResponse>>(paged, backend);

                var ttl = _timeProvider.GetUtcNow().AddSeconds(_cacheOptions.MetadataTtlSeconds > 0 ? _cacheOptions.MetadataTtlSeconds : 120);
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<SearchExecution<PagedResponse<EquipmentListingResponse>>>(searchExecutionResult, ttl));
            },
            cancellationToken);
    }
    /// <inheritdoc />
    public async Task<Result<PagedResponse<InventoryAssetResponse>>> GetListingAssetsAsync(
        Guid id,
        int page,
        int pageSize,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (caller.IsService || !caller.UserId.HasValue)
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ người dùng cá nhân mới có quyền xem thiết bị của Listing.");
        }

        int normalizedPage = Math.Max(1, page);
        int normalizedPageSize = Math.Clamp(pageSize, 1, 100);
        long offset = (long)(normalizedPage - 1) * normalizedPageSize;
        if (offset > int.MaxValue)
        {
            return Error.Validation("INVALID_PAGE", "Chỉ số trang vượt quá giới hạn hệ thống.");
        }
        int skip = (int)offset;

        string assetsScope = $"listing:{id:D}";
        string assetsQueryType = "owner-assets";
        string assetsHash = $"{normalizedPage}_{normalizedPageSize}";

        var pipelineResult = await _cache.GetOrCreateAsync<CachedListingAssetsPayload>(
            assetsScope,
            assetsQueryType,
            assetsHash,
            async (genToken, ct) =>
            {
                var listing = await _listingRepository.GetByIdAsync(id, ct);
                if (listing is null)
                {
                    return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
                }

                var assets = await _assetRepository.GetAssetsByListingIdAsync(id, skip, normalizedPageSize, ct);
                int totalCount = await _assetRepository.CountAssetsByListingIdAsync(id, ct);
                int totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling((double)totalCount / normalizedPageSize);

                var items = assets.Select(InventoryAssetMapper.ToResponse).ToList();
                var pagedResponse = new PagedResponse<InventoryAssetResponse>(items, normalizedPage, normalizedPageSize, totalCount, totalPages);
                var payload = new CachedListingAssetsPayload(listing.OwnerUserId, pagedResponse);

                var ttl = _timeProvider.GetUtcNow().AddSeconds(_cacheOptions.MetadataTtlSeconds > 0 ? _cacheOptions.MetadataTtlSeconds : 120);
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<CachedListingAssetsPayload>(payload, ttl));
            },
            cancellationToken);

        if (pipelineResult.IsFailure)
        {
            return pipelineResult.Error;
        }

        var cachedPayload = pipelineResult.Value;
        if (!InventoryAccessEvaluator.CanViewOrEditListing(caller, cachedPayload.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền xem thiết bị của Listing người dùng khác.");
        }

        return cachedPayload.Response;
    }
    /// <inheritdoc />
    public async Task<Result<EquipmentListingStatisticsResponse>> GetOwnerStatisticsAsync(
        EquipmentListingStatus? status,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (caller.IsAuthenticated && caller.IsService)
        {
            return Error.Forbidden("SERVICE_TOKEN_REJECTED", "Service token không được phép truy cập thống kê cá nhân.");
        }

        if (!caller.IsAuthenticated || !caller.UserId.HasValue)
        {
            return Error.Unauthorized("AUTH_REQUIRED", "Yêu cầu đăng nhập.");
        }

        if (status.HasValue && !Enum.IsDefined(status.Value))
        {
            return Error.Validation("INVALID_QUERY_PARAMETER", "Trạng thái không hợp lệ.");
        }
        Guid statsOwnerUserId = caller.UserId.Value;
        string statsScope = $"owner:{statsOwnerUserId:D}";
        string statsQueryType = "owner-stats";
        string statsHash = status.HasValue ? status.Value.ToString() : "all";

        return await _cache.GetOrCreateAsync<EquipmentListingStatisticsResponse>(
            statsScope,
            statsQueryType,
            statsHash,
            async (genToken, ct) =>
            {
                var criteria = new EquipmentListingStatisticsCriteria(
                    OwnerUserId: statsOwnerUserId,
                    Status: status);

                var result = await _listingRepository.GetListingStatisticsAsync(criteria, ct);
                var statsResult = new EquipmentListingStatisticsResponse(result.ByEquipmentType, result.ByBrand);
                var ttl = _timeProvider.GetUtcNow().AddSeconds(_cacheOptions.MetadataTtlSeconds > 0 ? _cacheOptions.MetadataTtlSeconds : 120);
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<EquipmentListingStatisticsResponse>(statsResult, ttl));
            },
            cancellationToken);
    }

    internal sealed record CachedListingAssetsPayload(
        Guid OwnerUserId,
        PagedResponse<InventoryAssetResponse> Response
    );
    /// <inheritdoc />
    public async Task<Result<EquipmentListingBatchResponse>> GetListingBatchAsync(
        EquipmentListingBatchRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (request is null)
        {
            return Error.Validation("INVALID_REQUEST", "Yêu cầu không được để trống.");
        }

        if (request.EquipmentListingIds is null || request.EquipmentListingIds.Count == 0)
        {
            return Error.Validation("EMPTY_EQUIPMENT_LISTING_IDS", "Danh sách equipmentListingIds bắt buộc và không được rỗng.");
        }

        if (request.EquipmentListingIds.Count > 100)
        {
            return Error.Validation("EXCEEDED_EQUIPMENT_LISTING_IDS_LIMIT", "Danh sách equipmentListingIds không được vượt quá 100 phần tử.");
        }

        if (request.EquipmentListingIds.Any(id => id == Guid.Empty))
        {
            return Error.Validation("INVALID_EQUIPMENT_LISTING_ID", "Danh sách equipmentListingIds chứa GUID rỗng không hợp lệ.");
        }

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (!InventoryAccessEvaluator.CanReadEquipmentListingBatch(caller))
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ Service với scope inventory.availability.read mới có quyền đọc batch listing.");
        }

        if (request.IncludeWarehouse && !InventoryAccessEvaluator.CanReadWarehouseBatch(caller))
        {
            return Error.Forbidden("FORBIDDEN", "Yêu cầu địa chỉ kho cần có scope inventory.warehouse.read.");
        }

        var deduplicatedIds = new List<Guid>();
        var seenIds = new HashSet<Guid>();
        foreach (var id in request.EquipmentListingIds)
        {
            if (seenIds.Add(id))
            {
                deduplicatedIds.Add(id);
            }
        }

        var listings = await _listingRepository.GetBatchByIdsAsync(deduplicatedIds, cancellationToken);
        var listingsById = listings.ToDictionary(l => l.Id);

        var items = new List<EquipmentListingBatchItemResponse>();
        var missingIds = new List<Guid>();

        foreach (var id in deduplicatedIds)
        {
            if (listingsById.TryGetValue(id, out var listing))
            {
                EquipmentListingBatchWarehouseDto? warehouseDto = null;
                if (request.IncludeWarehouse && listing.Warehouse != null)
                {
                    warehouseDto = new EquipmentListingBatchWarehouseDto(
                        listing.Warehouse.AddressDetail,
                        listing.Warehouse.ProvinceCode,
                        listing.Warehouse.ProvinceName,
                        listing.Warehouse.WardCode,
                        listing.Warehouse.WardName,
                        listing.Warehouse.Latitude,
                        listing.Warehouse.Longitude);
                }

                items.Add(new EquipmentListingBatchItemResponse(
                    listing.Id,
                    listing.OwnerUserId,
                    listing.EquipmentTypeId,
                    listing.Name,
                    listing.ThumbnailMediaId,
                    listing.DailyRate,
                    listing.Currency,
                    listing.Status.ToString(),
                    listing.DepositAmount,
                    warehouseDto));
            }
            else
            {
                missingIds.Add(id);
            }
        }

        return Result.Success(new EquipmentListingBatchResponse(items, missingIds));
    }
}
