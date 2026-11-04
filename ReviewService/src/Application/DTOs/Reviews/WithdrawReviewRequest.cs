namespace Dprental.ReviewService.Application.DTOs.Reviews;

/// <summary>
/// Yêu cầu rút lại đánh giá từ tác giả (chuyển authorState sang Withdrawn).
/// </summary>
/// <param name="Reason">Lý do tác giả rút lại đánh giá.</param>
public sealed record WithdrawReviewRequest(
    string? Reason = null);
