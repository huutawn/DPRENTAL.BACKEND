namespace Dprental.ReviewService.Application.DTOs.Reviews;

/// <summary>
/// DTO đại diện cho một mục thuê đang chờ người dùng viết đánh giá (GET /api/v1/reviews/pending).
/// </summary>
public sealed record PendingReviewItemResponse(
    Guid OrderId,
    Guid ItemId,
    Guid EquipmentListingId,
    Guid CounterpartId,
    string Direction,
    DateTimeOffset CompletedAt,
    DateTimeOffset DeadlineAt,
    int DaysRemaining);
