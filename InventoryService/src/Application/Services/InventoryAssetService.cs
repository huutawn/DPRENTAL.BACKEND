using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.IRepositories;

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
    private readonly TimeProvider _timeProvider;

    public InventoryAssetService(
        IInventoryAssetRepository assetRepository,
        IInventoryWriteTransaction writeTransaction,
        ICatalogServiceClient catalogClient,
        IInventoryMediaService mediaService,
        TimeProvider? timeProvider = null)
    {
        _assetRepository = assetRepository ?? throw new ArgumentNullException(nameof(assetRepository));
        _writeTransaction = writeTransaction ?? throw new ArgumentNullException(nameof(writeTransaction));
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _mediaService = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
        _timeProvider = timeProvider ?? TimeProvider.System;
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

        var asset = await _assetRepository.GetByIdAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return Error.NotFound("ASSET_NOT_FOUND", "Tài sản không tồn tại.");
        }

        if (!InventoryAccessEvaluator.CanManageAsset(caller, asset.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền truy cập tài sản này.");
        }

        return Result.Success(InventoryAssetMapper.ToResponse(asset));
    }

    /// <summary>
    /// Lấy danh sách tài sản phân trang của người dùng hiện tại (JWT sub).
    /// </summary>
    public async Task<Result<PagedResponse<InventoryAssetResponse>>> ListAssetsAsync(
        int page,
        int pageSize,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var authResult = AuthorizeManager(caller);
        if (authResult.IsFailure)
        {
            return authResult.Error;
        }

        if (page < 1)
        {
            return Error.Validation("INVALID_PAGE", "Số trang phải lớn hơn hoặc bằng 1.");
        }

        if (pageSize < 1 || pageSize > 100)
        {
            return Error.Validation("INVALID_PAGE_SIZE", "Kích thước trang phải từ 1 đến 100.");
        }

        long offset = (long)(page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            return Error.Validation("INVALID_PAGE", "Chỉ số trang vượt quá giới hạn hệ thống.");
        }

        int skip = (int)offset;
        var items = await _assetRepository.ListByOwnerAsync(caller.UserId!.Value, skip, pageSize, cancellationToken);
        var totalCount = await _assetRepository.CountByOwnerAsync(caller.UserId.Value, cancellationToken);
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling((double)totalCount / pageSize);

        var responseList = items.Select(InventoryAssetMapper.ToResponse).ToList();
        return Result.Success(new PagedResponse<InventoryAssetResponse>(responseList, page, pageSize, totalCount, totalPages));
    }

    private async Task<Result> ValidateCatalogSpecificationAsync(
        Guid equipmentTypeId,
        int schemaVersion,
        string specValues,
        CancellationToken cancellationToken)
    {
        var specResult = await _catalogClient.ValidateSpecificationAsync(equipmentTypeId, schemaVersion, specValues, cancellationToken);
        if (specResult.IsFailure)
        {
            return specResult.Error;
        }

        return specResult.Value.Status switch
        {
            CatalogSpecValidationStatus.Valid or CatalogSpecValidationStatus.NotSupported => Result.Success(),
            CatalogSpecValidationStatus.Invalid => Error.Validation("INVALID_SPECIFICATION", specResult.Value.Message ?? "Thông số kỹ thuật không hợp lệ."),
            _ => Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Trạng thái thẩm tra spec không hợp lệ.")
        };
    }

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
