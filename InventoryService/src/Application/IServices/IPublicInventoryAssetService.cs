using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.IServices;

/// <summary>
/// Giao diện dịch vụ ứng dụng đọc và khám phá tài sản công khai (Public Asset Discovery).
/// Hoạt động ẩn danh, không phụ thuộc vào ngữ cảnh người dùng gọi.
/// </summary>
public interface IPublicInventoryAssetService
{
    /// <summary>
    /// Tìm kiếm và lọc danh sách tài sản công khai theo điều kiện và khả dụng.
    /// </summary>
    Task<Result<PagedResponse<PublicInventoryAssetResponse>>> ListPublicAssetsAsync(
        ListPublicAssetsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy chi tiết thông tin tài sản công khai theo ID (chỉ tài sản AcceptingBookings = true).
    /// </summary>
    Task<Result<PublicInventoryAssetResponse>> GetPublicAssetByIdAsync(
        Guid assetId,
        CancellationToken cancellationToken = default);
}
