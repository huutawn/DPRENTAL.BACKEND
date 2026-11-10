using Dprental.RentalService.Application.DTOs.Reviews;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Clients;

/// <summary>
/// Client contract gọi ReviewService để kiểm tra trạng thái đánh giá theo lô.
/// </summary>
public interface IReviewRentalClient
{
    /// <summary>
    /// Gửi danh sách các cặp (orderId, itemId) tới ReviewService (POST /api/v1/reviews/status/batch).
    /// </summary>
    Task<Result<BatchReviewStatusResponse>> GetBatchReviewStatusAsync(
        BatchReviewStatusRequest request,
        CancellationToken cancellationToken = default);
}
