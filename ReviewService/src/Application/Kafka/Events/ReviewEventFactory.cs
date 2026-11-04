using System.Text.Json;
using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.Entities;

namespace Dprental.ReviewService.Application.Kafka.Events;

/// <summary>
/// Nhà máy kiến tạo sự kiện đóng góp công khai chuẩn hóa (Canonical Full-State Contribution Event).
/// Đảm bảo mọi đột biến đánh giá (Create, Update, Withdraw, Hide, Restore) đều phát cùng một cấu trúc payload
/// duy nhất trên topic review.events.v1, bảo vệ quyền riêng tư (không chứa PII hay nội dung bình luận).
/// </summary>
public static class ReviewEventFactory
{
    public const string PublicEventsTopic = "review.events.v1";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Tạo canonical full-state contribution event từ thực thể Review.
    /// Điều kiện publicEligible: Chiều renter_to_supplier_listing + AuthorState.Active + ModerationState.Visible.
    /// </summary>
    public static ReviewPublicContributionEvent CreateContributionEvent(Review review)
    {
        ArgumentNullException.ThrowIfNull(review);

        bool publicEligible = review.Direction == ReviewDirection.renter_to_supplier_listing &&
                              review.AuthorState == AuthorState.Active &&
                              review.ModerationState == ModerationState.Visible;

        return new ReviewPublicContributionEvent(
            review.Id,
            review.Version,
            review.ListingId,
            review.SupplierId,
            review.Rating,
            publicEligible);
    }
}
