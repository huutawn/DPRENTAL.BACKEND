using Dprental.RentalService.Application.DTOs.Orders;

namespace Dprental.RentalService.Application.Services.Orders;

/// <summary>
/// Service điều phối làm giàu trạng thái đánh giá (ReviewStatus) cho các mục thuê trong đơn hàng.
/// </summary>
public interface IOrderReviewStatusEnricher
{
    /// <summary>
    /// Lấy từ điển trạng thái đánh giá theo cặp (OrderId, ItemId). Mọi phần tử không tải được hoặc gặp lỗi sẽ mang trạng thái UNKNOWN.
    /// </summary>
    Task<IReadOnlyDictionary<(Guid OrderId, Guid ItemId), ReviewStatusDto>> FetchReviewStatusesAsync(
        IEnumerable<(Guid OrderId, Guid ItemId)> items,
        CancellationToken cancellationToken = default);
}
