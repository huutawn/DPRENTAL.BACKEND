using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Common;

namespace Dprental.ReviewService.Application.Services;

/// <summary>
/// Hợp đồng điều phối vòng đời tác giả đánh giá (Create, Update, Withdraw).
/// </summary>
public interface IReviewAuthoringService
{
    /// <summary>
    /// Tạo mới một đánh giá cho mục thuê đã hoàn tất nghiệp vụ.
    /// </summary>
    Task<Result<ReviewDetailResponse>> CreateReviewAsync(
        ReviewActorContext actor,
        CreateReviewRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Chỉnh sửa nội dung và điểm đánh giá đã tồn tại trong thời hạn cho phép.
    /// </summary>
    Task<Result<ReviewDetailResponse>> UpdateReviewAsync(
        ReviewActorContext actor,
        Guid reviewId,
        UpdateReviewRequest request,
        string idempotencyKey,
        string? ifMatch,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tác giả chủ động rút lại đánh giá (chuyển authorState sang Withdrawn).
    /// </summary>
    Task<Result<ReviewDetailResponse>> WithdrawReviewAsync(
        ReviewActorContext actor,
        Guid reviewId,
        WithdrawReviewRequest? request,
        string idempotencyKey,
        string? ifMatch,
        CancellationToken cancellationToken = default);
}
