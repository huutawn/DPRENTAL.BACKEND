namespace Dprental.ReviewService.Application.DTOs.Reviews;

/// <summary>
/// Yêu cầu kiểm tra trạng thái đánh giá theo lô (POST /api/v1/reviews/status/batch).
/// </summary>
public sealed record BatchReviewStatusRequest(
    IReadOnlyList<ReviewStatusItemRequest>? Items);

/// <summary>
/// Phần tử yêu cầu kiểm tra trạng thái đánh giá gồm cặp định danh đơn hàng và mục thuê.
/// </summary>
public sealed record ReviewStatusItemRequest(
    Guid OrderId,
    Guid ItemId);

/// <summary>
/// Kết quả trả về danh sách trạng thái đánh giá theo lô theo đúng thứ tự yêu cầu.
/// </summary>
public sealed record BatchReviewStatusResponse(
    IReadOnlyList<ReviewItemStatusResponse> Items);

/// <summary>
/// Trạng thái đánh giá cho từng mục thuê tương ứng trong yêu cầu.
/// </summary>
public sealed record ReviewItemStatusResponse(
    Guid OrderId,
    Guid ItemId,
    ReviewStatusDto ReviewStatus);

/// <summary>
/// Chi tiết trạng thái đánh giá của mục thuê.
/// </summary>
public sealed record ReviewStatusDto(
    string State,
    Guid? ReviewId,
    DateTimeOffset? DeadlineAt);

/// <summary>
/// Các hằng số trạng thái đánh giá trả về cho Client/FE.
/// </summary>
public static class ReviewStatusState
{
    public const string Available = "AVAILABLE";
    public const string Reviewed = "REVIEWED";
    public const string Expired = "EXPIRED";
    public const string NotEligible = "NOT_ELIGIBLE";
    public const string Unknown = "UNKNOWN";
}
