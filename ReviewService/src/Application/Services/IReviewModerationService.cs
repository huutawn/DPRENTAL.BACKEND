using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Common;

namespace Dprental.ReviewService.Application.Services;

/// <summary>
/// Hợp đồng điều phối các use case kiểm duyệt và xử lý báo cáo vi phạm (Report, Hide, Restore, Resolve).
/// </summary>
public interface IReviewModerationService
{
    /// <summary>
    /// Người dùng báo cáo một đánh giá vi phạm tiêu chuẩn cộng đồng.
    /// </summary>
    Task<Result<ReviewReportResponse>> CreateReportAsync(
        Guid reviewId,
        Guid reporterId,
        string reason,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Quản trị viên ẩn một đánh giá vi phạm.
    /// </summary>
    Task<Result<AdminModerationResponse>> HideReviewAsync(
        Guid reviewId,
        Guid adminId,
        string? reason,
        string ifMatch,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Quản trị viên phục hồi một đánh giá đã bị ẩn.
    /// </summary>
    Task<Result<AdminModerationResponse>> RestoreReviewAsync(
        Guid reviewId,
        Guid adminId,
        string? reason,
        string ifMatch,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đọc danh sách các báo cáo vi phạm phục vụ kiểm duyệt.
    /// </summary>
    Task<Result<PagedResult<ReviewReportResponse>>> GetReportsAsync(
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Xử lý quyết định đối với một báo cáo vi phạm.
    /// </summary>
    Task<Result<ReviewReportResponse>> ResolveReportAsync(
        Guid reportId,
        Guid adminId,
        string resolution,
        CancellationToken cancellationToken = default);
}
