using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Common;

namespace Dprental.ReviewService.Application.Clients;

/// <summary>
/// Hợp đồng client HTTP gọi sang RentalService để lấy snapshot dữ liệu mục thuê.
/// </summary>
public interface IRentalServiceClient
{
    /// <summary>
    /// Lấy snapshot thông tin mục thuê để kiểm tra điều kiện đánh giá.
    /// </summary>
    /// <param name="orderId">Mã đơn thuê.</param>
    /// <param name="itemId">Mã mục thuê.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Result chứa RentalItemReviewSnapshotDto hoặc Error tương ứng.</returns>
    Task<Result<RentalItemReviewSnapshotDto>> GetReviewSnapshotAsync(
        Guid orderId,
        Guid itemId,
        CancellationToken cancellationToken = default);
}
