using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Common;

namespace Dprental.ReviewService.Application.Services;

/// <summary>
/// Hợp đồng điều phối các use case truy vấn đánh giá (Public, Author, Received, Reputation, Summary, Pending).
/// </summary>
public interface IReviewQueryService
{
    /// <summary>
    /// Đọc chi tiết riêng tư của một đánh giá (dành cho Tác giả, Đối tượng nhận hoặc Admin).
    /// </summary>
    Task<Result<ReviewDetailResponse>> GetReviewDetailAsync(
        Guid reviewId,
        ReviewActorContext actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đọc danh sách đánh giá công khai của thiết bị.
    /// </summary>
    Task<Result<PagedResult<PublicReviewItemResponse>>> GetListingReviewsAsync(
        Guid listingId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đọc danh sách đánh giá công khai của nhà cung cấp.
    /// </summary>
    Task<Result<PagedResult<PublicReviewItemResponse>>> GetSupplierReviewsAsync(
        Guid supplierId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đọc danh sách đánh giá do chính người gọi tạo ra.
    /// </summary>
    Task<Result<PagedResult<ReviewDetailResponse>>> GetMyReviewsAsync(
        Guid actorId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    Task<Result<PagedResult<ReviewDetailResponse>>> GetMyReviewsAsync(
        ReviewActorContext actor,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// Lấy thống kê tổng hợp điểm đánh giá cho listing hoặc supplier.
    /// </summary>
    Task<Result<ReviewSummaryResponse>> GetSummaryAsync(
        string targetType,
        Guid targetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách mục thuê hoàn tất đang chờ người gọi đánh giá.
    /// </summary>
    Task<Result<PagedResult<PendingReviewItemResponse>>> GetPendingReviewsAsync(
        Guid actorId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách đánh giá người gọi nhận được từ đối tác.
    /// </summary>
    Task<Result<PagedResult<ReviewDetailResponse>>> GetReceivedReviewsAsync(
        Guid actorId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tính toán uy tín người thuê (điểm trung bình và phân bố từ supplier).
    /// </summary>
    Task<Result<RenterReputationResponse>> GetRenterReputationAsync(
        Guid renterId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Kiểm tra trạng thái đánh giá theo lô cho danh sách mục thuê (POST /api/v1/reviews/status/batch).
    /// </summary>
    Task<Result<BatchReviewStatusResponse>> GetBatchReviewStatusAsync(
        Guid actorId,
        BatchReviewStatusRequest request,
        CancellationToken cancellationToken = default);
}
