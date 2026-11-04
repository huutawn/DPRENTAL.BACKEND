using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Common;

namespace Dprental.ReviewService.Application.Services;

/// <summary>
/// Hợp đồng điều phối các use case cốt lõi của đánh giá (Review Application Workflows).
/// </summary>
public interface IReviewService
{
    /// <summary>
    /// Tạo mới một đánh giá cho mục thuê đã hoàn tất nghiệp vụ.
    /// </summary>
    /// <param name="actorId">Mã người dùng tạo đánh giá (phải là UUID người thuê hợp lệ).</param>
    /// <param name="request">Dữ liệu yêu cầu tạo đánh giá.</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp từ header Idempotency-Key.</param>
    /// <param name="userType">Loại token (không được là 'svc').</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Result chứa ReviewDetailResponse nếu thành công, hoặc Error tương ứng.</returns>
    Task<Result<ReviewDetailResponse>> CreateReviewAsync(
        Guid actorId,
        CreateReviewRequest request,
        string idempotencyKey,
        string? userType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tạo mới một đánh giá cho mục thuê đã hoàn tất nghiệp vụ (với actorId dạng chuỗi).
    /// </summary>
    Task<Result<ReviewDetailResponse>> CreateReviewAsync(
        string? actorId,
        CreateReviewRequest request,
        string idempotencyKey,
        string? userType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tạo mới một đánh giá cho mục thuê với ngữ cảnh người dùng.
    /// </summary>
    Task<Result<ReviewDetailResponse>> CreateReviewAsync(
        ReviewActorContext actor,
        CreateReviewRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Chỉnh sửa nội dung và điểm đánh giá đã tồn tại trong thời hạn cho phép.
    /// </summary>
    /// <param name="actorId">Mã người dùng (phải là tác giả của đánh giá).</param>
    /// <param name="reviewId">Mã định danh đánh giá cần chỉnh sửa.</param>
    /// <param name="request">Dữ liệu nội dung và điểm cập nhật.</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp.</param>
    /// <param name="ifMatch">Header If-Match chứa ETag phiên bản hiện tại.</param>
    /// <param name="userType">Loại token.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Result chứa ReviewDetailResponse đã cập nhật hoặc Error tương ứng.</returns>
    Task<Result<ReviewDetailResponse>> UpdateReviewAsync(
        Guid actorId,
        Guid reviewId,
        UpdateReviewRequest request,
        string idempotencyKey,
        string? ifMatch,
        string? userType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Chỉnh sửa đánh giá (với actorId dạng chuỗi).
    /// </summary>
    Task<Result<ReviewDetailResponse>> UpdateReviewAsync(
        string? actorId,
        Guid reviewId,
        UpdateReviewRequest request,
        string idempotencyKey,
        string? ifMatch,
        string? userType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Chỉnh sửa đánh giá với ngữ cảnh người dùng.
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
    /// <param name="actorId">Mã người dùng (phải là tác giả của đánh giá).</param>
    /// <param name="reviewId">Mã định danh đánh giá cần rút.</param>
    /// <param name="request">Yêu cầu rút đánh giá kèm lý do (tùy chọn).</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp.</param>
    /// <param name="ifMatch">Header If-Match chứa ETag phiên bản hiện tại.</param>
    /// <param name="userType">Loại token.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Result chứa ReviewDetailResponse đã chuyển sang Withdrawn hoặc Error tương ứng.</returns>
    Task<Result<ReviewDetailResponse>> WithdrawReviewAsync(
        Guid actorId,
        Guid reviewId,
        WithdrawReviewRequest? request,
        string idempotencyKey,
        string? ifMatch,
        string? userType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rút lại đánh giá (với actorId dạng chuỗi).
    /// </summary>
    Task<Result<ReviewDetailResponse>> WithdrawReviewAsync(
        string? actorId,
        Guid reviewId,
        WithdrawReviewRequest? request,
        string idempotencyKey,
        string? ifMatch,
        string? userType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rút lại đánh giá với ngữ cảnh người dùng.
    /// </summary>
    Task<Result<ReviewDetailResponse>> WithdrawReviewAsync(
        ReviewActorContext actor,
        Guid reviewId,
        WithdrawReviewRequest? request,
        string idempotencyKey,
        string? ifMatch,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đọc thông tin chi tiết đánh giá riêng tư (chỉ Tác giả, Đối tượng nhận hoặc Quản trị viên).
    /// </summary>
    /// <param name="reviewId">Mã định danh đánh giá.</param>
    /// <param name="actorId">Mã người dùng gửi yêu cầu.</param>
    /// <param name="isAdmin">Cờ xác định vai trò quản trị viên.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Result chứa ReviewDetailResponse nếu được phép, hoặc 404 REVIEW_NOT_FOUND cho người ngoài.</returns>
    Task<Result<ReviewDetailResponse>> GetReviewDetailAsync(
        Guid reviewId,
        Guid actorId,
        bool isAdmin = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đọc thông tin chi tiết đánh giá riêng tư (với actorId dạng chuỗi).
    /// </summary>
    Task<Result<ReviewDetailResponse>> GetReviewDetailAsync(
        Guid reviewId,
        string? actorId,
        bool isAdmin = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đọc thông tin chi tiết đánh giá riêng tư với ngữ cảnh người dùng.
    /// </summary>
    Task<Result<ReviewDetailResponse>> GetReviewDetailAsync(
        Guid reviewId,
        ReviewActorContext actor,
        CancellationToken cancellationToken = default);
}
