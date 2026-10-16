using System.Text.Json;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Application.Search;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Application.Services;

/// <summary>
/// Application service điều phối nghiệp vụ quản lý tài sản (InventoryAsset).
/// Phần triển khai các tác vụ đọc (Get, List) và các hàm kiểm tra dùng chung.
/// </summary>
public sealed partial class InventoryAssetService : IInventoryAssetService
{
    private readonly IInventoryAssetRepository _assetRepository;
    private readonly IInventoryWriteTransaction _writeTransaction;
    private readonly ICatalogServiceClient _catalogClient;
    private readonly IInventoryMediaService _mediaService;
    private readonly AssetSearchQueryValidator _queryValidator;
    private readonly AssetSearchFilterResolver _filterResolver;
    private readonly TimeProvider _timeProvider;
    private readonly IInventorySearchIndexService? _searchIndexService;
    private readonly ILogger<InventoryAssetService>? _logger;
    private readonly Dprental.InventoryService.Application.Caching.IInventoryReadCache _cache;
    private readonly Dprental.InventoryService.Application.Caching.InventoryCacheOptions _cacheOptions;

    public InventoryAssetService(
        IInventoryAssetRepository assetRepository,
        IInventoryWriteTransaction writeTransaction,
        ICatalogServiceClient catalogClient,
        IInventoryMediaService mediaService,
        AssetSearchQueryValidator queryValidator,
        AssetSearchFilterResolver filterResolver,
        TimeProvider? timeProvider = null,
        IInventorySearchIndexService? searchIndexService = null,
        ILogger<InventoryAssetService>? logger = null)
        : this(assetRepository, writeTransaction, catalogClient, mediaService, queryValidator, filterResolver, null, null, timeProvider, searchIndexService, logger)
    {
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public InventoryAssetService(
        IInventoryAssetRepository assetRepository,
        IInventoryWriteTransaction writeTransaction,
        ICatalogServiceClient catalogClient,
        IInventoryMediaService mediaService,
        AssetSearchQueryValidator queryValidator,
        AssetSearchFilterResolver filterResolver,
        Dprental.InventoryService.Application.Caching.IInventoryReadCache? cache = null,
        Microsoft.Extensions.Options.IOptions<Dprental.InventoryService.Application.Caching.InventoryCacheOptions>? cacheOptions = null,
        TimeProvider? timeProvider = null,
        IInventorySearchIndexService? searchIndexService = null,
        ILogger<InventoryAssetService>? logger = null)
    {
        _assetRepository = assetRepository ?? throw new ArgumentNullException(nameof(assetRepository));
        _writeTransaction = writeTransaction ?? throw new ArgumentNullException(nameof(writeTransaction));
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _mediaService = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
        _queryValidator = queryValidator ?? throw new ArgumentNullException(nameof(queryValidator));
        _filterResolver = filterResolver ?? throw new ArgumentNullException(nameof(filterResolver));
        _cache = cache ?? Dprental.InventoryService.Application.Caching.NoOpInventoryReadCache.Instance;
        _cacheOptions = cacheOptions?.Value ?? new Dprental.InventoryService.Application.Caching.InventoryCacheOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _searchIndexService = searchIndexService;
        _logger = logger;
    }

    /// <summary>
    /// Lấy chi tiết tài sản theo ID với phân quyền truy cập.
    /// </summary>
    public async Task<Result<InventoryAssetResponse>> GetAssetByIdAsync(
        Guid assetId,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var callerCheck = CheckAuthenticatedHuman(caller);
        if (callerCheck.IsFailure)
        {
            return callerCheck.Error;
        }

        string scope = $"asset:{assetId:D}";
        string queryType = "asset-detail";
        string inputHash = "detail";

        var pipelineResult = await _cache.GetOrCreateAsync<CachedAssetDetailPayload>(
            scope,
            queryType,
            inputHash,
            async (genToken, ct) =>
            {
                var asset = await _assetRepository.GetByIdAsync(assetId, ct);
                if (asset is null)
                {
                    return Error.NotFound("ASSET_NOT_FOUND", "Tài sản không tồn tại.");
                }

                var response = InventoryAssetMapper.ToResponse(asset);
                var ttl = _timeProvider.GetUtcNow().AddSeconds(_cacheOptions.MetadataTtlSeconds > 0 ? _cacheOptions.MetadataTtlSeconds : 120);
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<CachedAssetDetailPayload>(
                    new CachedAssetDetailPayload(asset.Listing.OwnerUserId, response),
                    ttl));
            },
            cancellationToken);

        if (pipelineResult.IsFailure)
        {
            return pipelineResult.Error;
        }

        var payload = pipelineResult.Value;
        if (!InventoryAccessEvaluator.CanManageAsset(caller, payload.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền truy cập tài sản này.");
        }

        return payload.Response;
    }
    /// <summary>
    /// Lấy danh sách tài sản phân trang của người dùng hiện tại (JWT sub).
    /// </summary>
    public async Task<Result<SearchExecution<PagedResponse<InventoryAssetResponse>>>> ListAssetsAsync(
        ListAssetsRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        var authResult = AuthorizeManager(caller);
        if (authResult.IsFailure)
        {
            return authResult.Error;
        }

        Guid ownerUserId = caller.UserId!.Value;

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

        var structCheck = AssetSearchFilterResolver.ValidateStructure(request.Filters, validated.EquipmentTypeId);
        if (structCheck.IsFailure)
        {
            return structCheck.Error;
        }

        string searchScope = $"owner:{ownerUserId:D}";
        string searchQueryType = "owner-assets";
        string searchHash = Dprental.InventoryService.Application.Caching.CacheKeyHelper.ComputeOwnerAssetSearchHash(
            request.Page,
            request.PageSize,
            request.Q,
            request.EquipmentTypeId,
            request.BrandId,
            request.ManufactureYearFrom,
            request.ManufactureYearTo,
            request.AcceptingBookings,
            request.Sort,
            request.Filters);

        return await _cache.GetOrCreateAsync<SearchExecution<PagedResponse<InventoryAssetResponse>>>(
            searchScope,
            searchQueryType,
            searchHash,
            async (genToken, ct) =>
            {
                IReadOnlyList<Guid>? candidateIds = null;
                string backend = "postgres";

                if (!string.IsNullOrWhiteSpace(validated.SearchKeyword) && _searchIndexService != null)
                {
                    var candidateResult = await _searchIndexService.SearchOwnerAssetCandidatesAsync(
                        new OwnerAssetSearchIndexQuery(
                            ownerUserId,
                            validated.SearchKeyword,
                            validated.EquipmentTypeId,
                            validated.BrandId,
                            request.AcceptingBookings,
                            validated.ManufactureYearFrom,
                            validated.ManufactureYearTo),
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
                            "Meilisearch query failed or candidates truncated for query '{Keyword}'. Falling back to PostgreSQL.",
                            validated.SearchKeyword);
                        candidateIds = null;
                        backend = "postgres";
                    }
                }

                var resolveResult = await _filterResolver.ResolveAsync(
                    request.Filters,
                    validated.EquipmentTypeId,
                    cancellationToken => _assetRepository.GetDistinctSchemaVersionsForOwnerAsync(
                        new OwnerAssetSearchCriteria(
                            ownerUserId,
                            new AssetSearchFilters(
                                candidateIds == null ? validated.SearchKeyword : null,
                                validated.EquipmentTypeId,
                                validated.BrandId,
                                validated.ManufactureYearFrom,
                                validated.ManufactureYearTo,
                                CandidateIds: candidateIds),
                            acceptingBookings: request.AcceptingBookings),
                        cancellationToken),
                    isFieldForbidden: null,
                    ct);
                if (resolveResult.IsFailure)
                {
                    return resolveResult.Error;
                }
                var resolvedFilters = resolveResult.Value;

                var searchFilters = new AssetSearchFilters(
                    SearchKeyword: candidateIds == null ? validated.SearchKeyword : null,
                    EquipmentTypeId: validated.EquipmentTypeId,
                    BrandId: validated.BrandId,
                    ManufactureYearFrom: validated.ManufactureYearFrom,
                    ManufactureYearTo: validated.ManufactureYearTo,
                    Sort: validated.Sort,
                    CompatibleSchemaVersions: resolvedFilters.CompatibleSchemaVersions,
                    DynamicFilters: resolvedFilters.Criteria,
                    CandidateIds: candidateIds);

                var searchCriteria = new OwnerAssetSearchCriteria(
                    ownerUserId: ownerUserId,
                    filters: searchFilters,
                    acceptingBookings: request.AcceptingBookings,
                    skip: validated.Skip,
                    take: validated.Take);
                var items = await _assetRepository.SearchForOwnerAsync(searchCriteria, ct);
                var totalCount = await _assetRepository.CountForOwnerAsync(searchCriteria, ct);
                int totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling((double)totalCount / validated.Take);

                var responseList = items.Select(InventoryAssetMapper.ToResponse).ToList();
                var paged = new PagedResponse<InventoryAssetResponse>(responseList, request.Page, request.PageSize, totalCount, totalPages);
                var searchExecutionResult = new SearchExecution<PagedResponse<InventoryAssetResponse>>(paged, backend);

                var ttl = _timeProvider.GetUtcNow().AddSeconds(_cacheOptions.MetadataTtlSeconds > 0 ? _cacheOptions.MetadataTtlSeconds : 120);
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<SearchExecution<PagedResponse<InventoryAssetResponse>>>(searchExecutionResult, ttl));
            },
            cancellationToken);
    }

    internal sealed record CachedAssetDetailPayload(
        Guid OwnerUserId,
        InventoryAssetResponse Response
    );


    private static Result CheckAuthenticatedHuman(IInventoryCallerContext caller)
    {
        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (!caller.IsHuman || caller.UserId is null || caller.UserId == Guid.Empty)
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ người dùng cá nhân hợp lệ mới được thực hiện thao tác này.");
        }

        return Result.Success();
    }

    private static Result AuthorizeManager(IInventoryCallerContext caller)
    {
        var check = CheckAuthenticatedHuman(caller);
        if (check.IsFailure)
        {
            return check;
        }

        if (!caller.IsAdmin && !caller.Permissions.Contains(InventoryAccessEvaluator.PermissionAssetManage))
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền quản lý tài sản trong kho.");
        }

        return Result.Success();
    }
}
