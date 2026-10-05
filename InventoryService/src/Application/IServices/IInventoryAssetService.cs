using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.IServices;

/// <summary>
/// Hợp đồng service quản lý vòng đời và thông tin tài sản trong kho.
/// </summary>
public interface IInventoryAssetService
{
    Task<Result<InventoryAssetResponse>> CreateAssetAsync(
        CreateInventoryAssetRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result<InventoryAssetResponse>> GetAssetByIdAsync(
        Guid assetId,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result<PagedResponse<InventoryAssetResponse>>> ListAssetsAsync(
        int page,
        int pageSize,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result<InventoryAssetResponse>> PatchAssetAsync(
        Guid assetId,
        PatchInventoryAssetRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result<InventoryAssetResponse>> ToggleAcceptingBookingsAsync(
        Guid assetId,
        ToggleAcceptingBookingsRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);
}
