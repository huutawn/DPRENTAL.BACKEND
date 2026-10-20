using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.IServices;

/// <summary>
/// Application service xử lý use cases truy vấn tính khả dụng và busy ranges của tài sản theo DPR-150.
/// </summary>
public interface IInventoryAvailabilityService
{
    /// <summary>
    /// Kiểm tra tính khả dụng hàng loạt tài sản đối với một khoảng thời gian đề xuất.
    /// Luôn đọc trực tiếp từ PostgreSQL.
    /// </summary>
    Task<Result<CheckAvailabilityBatchResponse>> CheckAvailabilityBatchAsync(
        CheckAvailabilityBatchRequest request,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// Kiểm tra tính khả dụng của một EquipmentListing đối với khoảng thời gian và số lượng đề xuất.
    /// </summary>
    Task<Result<CheckListingAvailabilityResponse>> CheckListingAvailabilityAsync(
        Guid listingId,
        CheckListingAvailabilityRequest request,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Lấy danh sách các khoảng bận đã được làm sạch của tài sản trong khoảng thời gian [from, to].
    /// Sử dụng Valkey read cache theo từng tháng với fallback PostgreSQL.
    /// Luôn đọc existence và AcceptingBookings từ PostgreSQL.
    /// </summary>
    Task<Result<AssetBusyRangesResponse>> GetBusyRangesAsync(
        Guid assetId,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken = default);
}
