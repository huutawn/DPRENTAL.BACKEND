using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.Search;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.IServices;

/// <summary>
/// Giao diện dịch vụ ứng dụng tìm kiếm, lọc và đọc chi tiết EquipmentListing công khai (Public Equipment Listing Discovery).
/// Vận hành ẩn danh và độc lập với thông tin định danh caller.
/// </summary>
public interface IPublicEquipmentListingService
{
    /// <summary>
    /// Lấy chi tiết một EquipmentListing công khai theo ID (chỉ trạng thái Active).
    /// </summary>
    Task<Result<PublicEquipmentListingResponse>> GetPublicListingByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tìm kiếm và khám phá danh sách EquipmentListing công khai theo bộ lọc và tính khả dụng.
    /// </summary>
    Task<Result<SearchExecution<PagedResponse<PublicEquipmentListingResponse>>>> ListPublicListingsAsync(
        ListPublicEquipmentListingsRequest request,
        IInventoryCallerContext callerContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Thống kê số lượng listing đang hoạt động (Active) trên toàn hệ thống nhóm theo equipmentTypeId và brandId.
    /// </summary>
    Task<Result<EquipmentListingStatisticsResponse>> GetStatisticsAsync(
        CancellationToken cancellationToken = default);
}
