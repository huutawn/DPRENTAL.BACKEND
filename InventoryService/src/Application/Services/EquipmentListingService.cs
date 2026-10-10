using System.Linq;
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
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Policies;
using Microsoft.Extensions.Options;

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

    public EquipmentListingService(
        IEquipmentListingRepository listingRepository,
        IInventoryAssetRepository assetRepository,
        IInventoryWriteTransaction writeTransaction,
        ICatalogServiceClient catalogClient,
        IInventoryMediaService mediaService,
        IOptions<MediaServiceOptions> mediaOptions,
        TimeProvider? timeProvider = null)
    {
        _listingRepository = listingRepository ?? throw new ArgumentNullException(nameof(listingRepository));
        _assetRepository = assetRepository ?? throw new ArgumentNullException(nameof(assetRepository));
        _writeTransaction = writeTransaction ?? throw new ArgumentNullException(nameof(writeTransaction));
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _mediaService = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
        ArgumentNullException.ThrowIfNull(mediaOptions);
        _mediaOptions = mediaOptions.Value ?? throw new ArgumentException("MediaServiceOptions không được null.", nameof(mediaOptions));
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        var listing = await _listingRepository.GetByIdAsync(id, cancellationToken);
        if (listing is null)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        if (!InventoryAccessEvaluator.CanViewOrEditListing(caller, listing.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền truy cập Listing của người dùng khác.");
        }
        int totalQuantity = await _listingRepository.GetTotalQuantityAsync(id, cancellationToken);
        return EquipmentListingMapper.ToResponse(listing, totalQuantity, _mediaOptions.PublicGatewayUrl);
    }

    /// <inheritdoc />
    public async Task<Result<PagedResponse<EquipmentListingResponse>>> ListOwnerListingsAsync(
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
        int page = Math.Max(1, request.Page);
        int pageSize = Math.Clamp(request.PageSize, 1, 100);
        int skip = (page - 1) * pageSize;

        var listings = await _listingRepository.ListForOwnerAsync(caller.UserId.Value, skip, pageSize, cancellationToken);
        int totalCount = await _listingRepository.CountForOwnerAsync(caller.UserId.Value, cancellationToken);
        int totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling((double)totalCount / pageSize);

        var items = new List<EquipmentListingResponse>(listings.Count);
        foreach (var l in listings)
        {
            int totalQty = await _listingRepository.GetTotalQuantityAsync(l.Id, cancellationToken);
            items.Add(EquipmentListingMapper.ToResponse(l, totalQty, _mediaOptions.PublicGatewayUrl));
        }

        return new PagedResponse<EquipmentListingResponse>(items, page, pageSize, totalCount, totalPages);
    }

    /// <inheritdoc />
    public async Task<Result<PagedResponse<InventoryAssetResponse>>> GetListingAssetsAsync(
        Guid id,
        DateOnly? fromDate,
        DateOnly? toDate,
        string? status,
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

        bool hasFrom = fromDate.HasValue;
        bool hasTo = toDate.HasValue;
        bool hasStatus = !string.IsNullOrWhiteSpace(status);
        bool hasAnyFilter = hasFrom || hasTo || hasStatus;

        DateOnly validStart = default;
        DateOnly validEnd = default;
        string normalizedStatus = string.Empty;

        if (hasAnyFilter)
        {
            if (!hasFrom || !hasTo || !hasStatus)
            {
                return Error.Validation(
                    "INVALID_FILTER_PARAMETERS",
                    "Lọc thiết bị theo ngày yêu cầu cung cấp đầy đủ cả 'from', 'to' và 'status'.");
            }

            var dateValResult = AvailabilityDateValidator.ValidateDateWindow(fromDate, toDate, out validStart, out validEnd);
            if (dateValResult.IsFailure)
            {
                return dateValResult.Error;
            }

            normalizedStatus = status!.Trim().ToLowerInvariant();
            if (normalizedStatus is not ("available" or "rented" or "held" or "blocked"))
            {
                return Error.Validation(
                    "INVALID_FILTER_STATUS",
                    "Giá trị 'status' không hợp lệ. Các giá trị được chấp nhận: available, rented, held, blocked.");
            }
        }

        int normalizedPage = Math.Max(1, page);
        int normalizedPageSize = Math.Clamp(pageSize, 1, 100);
        int skip = (normalizedPage - 1) * normalizedPageSize;

        var listing = await _listingRepository.GetByIdAsync(id, cancellationToken);
        if (listing is null)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        if (!InventoryAccessEvaluator.CanViewOrEditListing(caller, listing.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền xem thiết bị của Listing người dùng khác.");
        }

        if (!hasAnyFilter)
        {
            var assets = await _assetRepository.GetAssetsByListingIdAsync(id, skip, normalizedPageSize, cancellationToken);
            int totalCount = await _assetRepository.CountAssetsByListingIdAsync(id, cancellationToken);
            int totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling((double)totalCount / normalizedPageSize);

            var items = assets.Select(InventoryAssetMapper.ToResponse).ToList();
            return new PagedResponse<InventoryAssetResponse>(items, normalizedPage, normalizedPageSize, totalCount, totalPages);
        }
        var evaluationNowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());

        var (filteredAssets, filteredTotalCount) = await _assetRepository.GetFilteredAssetsByListingIdAsync(
            id,
            validStart,
            validEnd,
            normalizedStatus,
            evaluationNowUtc,
            skip,
            normalizedPageSize,
            cancellationToken);

        int filteredTotalPages = filteredTotalCount == 0 ? 0 : (int)Math.Ceiling((double)filteredTotalCount / normalizedPageSize);

        var pagedItems = filteredAssets
            .Select(InventoryAssetMapper.ToResponse)
            .ToList();

        return new PagedResponse<InventoryAssetResponse>(
            pagedItems,
            normalizedPage,
            normalizedPageSize,
            filteredTotalCount,
            filteredTotalPages);
    }

    /// <inheritdoc />
    public Task<Result<PagedResponse<InventoryAssetResponse>>> GetListingAssetsAsync(
        Guid id,
        int page,
        int pageSize,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default) =>
        GetListingAssetsAsync(id, null, null, null, page, pageSize, caller, cancellationToken);
}
