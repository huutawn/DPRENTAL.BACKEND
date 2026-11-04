namespace Dprental.ReviewService.Application.Kafka.Events;

/// <summary>
/// Sự kiện công khai đóng góp điểm rating cho listing và supplier trên topic review.events.v1.
/// Full-state contribution event cho phép consumer deduplicate và idempotent projection.
/// Tuyệt đối không chứa comment riêng tư hay danh tính người thuê.
/// </summary>
public sealed record ReviewPublicContributionEvent(
    Guid ReviewId,
    long ReviewVersion,
    Guid ListingId,
    Guid SupplierId,
    int Rating,
    bool PublicEligible);

/// <summary>
/// Sự kiện riêng tư cho NotificationService và các consumer nội bộ được cấp quyền trên topic review.private.events.v1.
/// </summary>
public sealed record ReviewPrivateLifecycleEvent(
    Guid ReviewId,
    long ReviewVersion,
    string Direction,
    Guid AuthorId,
    Guid RecipientId,
    Guid OrderId,
    Guid ItemId,
    string EventType);
