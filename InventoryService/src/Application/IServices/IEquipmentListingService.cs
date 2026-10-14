using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Search;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Application.IServices;

/// <summary>
/// Giao diện dịch vụ ứng dụng quản lý danh mục niêm yết thiết bị (EquipmentListing) và các đơn vị vật lý.
/// </summary>
public interface IEquipmentListingService
{
    /// <summary>
    /// Tạo mới một EquipmentListing ở trạng thái Draft cùng một đơn vị thiết bị vật lý ban đầu qua số serial.
    /// </summary>
    Task<Result<EquipmentListingResponse>> CreateListingAsync(
        CreateEquipmentListingRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy thông tin chi tiết của một EquipmentListing kèm derived totalQuantity.
    /// </summary>
    Task<Result<EquipmentListingResponse>> GetListingByIdAsync(
        Guid id,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách phân trang các EquipmentListing thuộc sở hữu của người dùng hiện tại.
    /// </summary>
    Task<Result<SearchExecution<PagedResponse<EquipmentListingResponse>>>> ListOwnerListingsAsync(
        ListEquipmentListingsRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cập nhật một phần thông tin chung hoặc trạng thái của EquipmentListing (PATCH presence tracking).
    /// </summary>
    Task<Result<EquipmentListingResponse>> PatchListingAsync(
        Guid id,
        PatchEquipmentListingRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cập nhật giá thuê niêm yết của EquipmentListing (thay thế toàn bộ).
    /// </summary>
    Task<Result<RentalRateDto>> UpdateRentalRateAsync(
        Guid id,
        RentalRateDto request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Thêm một đơn vị thiết bị vật lý vào một EquipmentListing hiện có bằng số serial.
    /// </summary>
    Task<Result<InventoryAssetResponse>> AddAssetAsync(
        Guid id,
        AddListingAssetRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách phân trang các đơn vị thiết bị vật lý thuộc một EquipmentListing.
    /// </summary>
    Task<Result<PagedResponse<InventoryAssetResponse>>> GetListingAssetsAsync(
        Guid id,
        int page,
        int pageSize,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Thống kê số lượng listing của người dùng hiện tại nhóm theo equipmentTypeId và brandId, lọc trạng thái tùy chọn.
    /// </summary>
    Task<Result<EquipmentListingStatisticsResponse>> GetOwnerStatisticsAsync(
        EquipmentListingStatus? status,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);
}
