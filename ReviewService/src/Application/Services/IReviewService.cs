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

    /// <summary>
    /// Lấy danh sách đánh giá công khai của một thiết bị (kèm phân trang).
    /// </summary>
    /// <param name="listingId">Mã thiết bị (UUID).</param>
    /// <param name="page">Số trang (mặc định 1).</param>
    /// <param name="pageSize">Kích thước trang (mặc định 20, tối đa 50).</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Result chứa PagedResult của PublicReviewItemResponse.</returns>
    Task<Result<PagedResult<PublicReviewItemResponse>>> GetListingReviewsAsync(
        Guid listingId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách đánh giá công khai của một nhà cung cấp (kèm phân trang).
    /// </summary>
    /// <param name="supplierId">Mã nhà cung cấp (UUID).</param>
    /// <param name="page">Số trang (mặc định 1).</param>
    /// <param name="pageSize">Kích thước trang (mặc định 20, tối đa 50).</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Result chứa PagedResult của PublicReviewItemResponse.</returns>
    Task<Result<PagedResult<PublicReviewItemResponse>>> GetSupplierReviewsAsync(
        Guid supplierId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách đánh giá do chính người dùng hiện tại viết (kèm phân trang, bao gồm cả Active và Withdrawn).
    /// </summary>
    /// <param name="actorId">Mã người dùng (UUID).</param>
    /// <param name="page">Số trang (mặc định 1).</param>
    /// <param name="pageSize">Kích thước trang (mặc định 20, tối đa 50).</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Result chứa PagedResult của ReviewDetailResponse.</returns>
    Task<Result<PagedResult<ReviewDetailResponse>>> GetMyReviewsAsync(
        Guid actorId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách đánh giá của chính người dùng (với actorId dạng chuỗi).
    /// </summary>
    Task<Result<PagedResult<ReviewDetailResponse>>> GetMyReviewsAsync(
        string? actorId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách đánh giá của chính người dùng với ngữ cảnh người dùng.
    /// </summary>
    Task<Result<PagedResult<ReviewDetailResponse>>> GetMyReviewsAsync(
        ReviewActorContext actor,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tính toán và lấy thông tin tổng hợp điểm đánh giá cho thiết bị hoặc nhà cung cấp.
    /// </summary>
    /// <param name="targetType">Loại đối tượng ('listing' hoặc 'supplier', không phân biệt hoa thường).</param>
    /// <param name="targetId">Mã định danh đối tượng (UUID).</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Result chứa ReviewSummaryResponse nếu hợp lệ, hoặc Error.Validation nếu targetType không hợp lệ.</returns>
    Task<Result<ReviewSummaryResponse>> GetSummaryAsync(
        string targetType,
        Guid targetId,
        CancellationToken cancellationToken = default);

    Task<Result<PagedResult<PendingReviewItemResponse>>> GetPendingReviewsAsync(
        Guid actorId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    Task<Result<PagedResult<ReviewDetailResponse>>> GetReceivedReviewsAsync(
        Guid actorId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    Task<Result<RenterReputationResponse>> GetRenterReputationAsync(
        Guid renterId,
        CancellationToken cancellationToken = default);

    Task<Result<ReviewReportResponse>> CreateReportAsync(
        Guid reviewId,
        Guid reporterId,
        string reason,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<Result<AdminModerationResponse>> HideReviewAsync(
        Guid reviewId,
        Guid adminId,
        string? reason,
        string ifMatch,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<Result<AdminModerationResponse>> RestoreReviewAsync(
        Guid reviewId,
        Guid adminId,
        string? reason,
        string ifMatch,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<Result<PagedResult<ReviewReportResponse>>> GetReportsAsync(
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    Task<Result<ReviewReportResponse>> ResolveReportAsync(
        Guid reportId,
        Guid adminId,
        string resolution,
        CancellationToken cancellationToken = default);

    Task<Result<ReviewMediaResponse>> AttachMediaAsync(
        Guid reviewId,
        Guid actorId,
        Guid mediaId,
        string? mediaType,
        string ifMatch,
        CancellationToken cancellationToken = default);

    Task<Result> DetachMediaAsync(
        Guid reviewId,
        Guid actorId,
        Guid mediaId,
        string ifMatch,
        CancellationToken cancellationToken = default);
}
