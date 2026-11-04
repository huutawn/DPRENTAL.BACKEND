using Dprental.ReviewService.Domain.Entities;

namespace Dprental.ReviewService.Application.DTOs.Reviews;

/// <summary>
/// DTO phản hồi mục đánh giá công khai cho thiết bị hoặc nhà cung cấp (Allowlist).
/// Không làm lộ thông tin riêng tư của tác giả, mặc định sử dụng tên ẩn danh trung tính 'Khách hàng'.
/// </summary>
/// <param name="ReviewId">Mã định danh đánh giá (UUID).</param>
/// <param name="Rating">Điểm đánh giá từ 1 đến 5 sao.</param>
/// <param name="Comment">Nội dung bình luận.</param>
/// <param name="CreatedAt">Thời điểm tạo đánh giá (UTC).</param>
/// <param name="UpdatedAt">Thời điểm cập nhật gần nhất (UTC).</param>
/// <param name="AuthorMaskedName">Tên tác giả ẩn danh trung tính (mặc định: 'Khách hàng').</param>
public sealed record PublicReviewItemResponse(
    Guid ReviewId,
    int Rating,
    string? Comment,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string AuthorMaskedName = "Khách hàng")
{
    /// <summary>
    /// Khởi tạo mặc định phục vụ deserialization.
    /// </summary>
    public PublicReviewItemResponse()
        : this(Guid.Empty, 0, null, default, null, "Khách hàng")
    {
    }

    /// <summary>
    /// Ánh xạ từ thực thể Domain Review sang PublicReviewItemResponse.
    /// </summary>
    /// <param name="review">Thực thể Review.</param>
    /// <param name="authorMaskedName">Tên ẩn danh của tác giả (mặc định: 'Khách hàng').</param>
    /// <returns>Đối tượng PublicReviewItemResponse.</returns>
    public static PublicReviewItemResponse FromEntity(Review review, string authorMaskedName = "Khách hàng")
    {
        ArgumentNullException.ThrowIfNull(review);

        return new PublicReviewItemResponse(
            review.Id,
            review.Rating,
            review.Comment,
            review.CreatedAt,
            review.UpdatedAt,
            string.IsNullOrWhiteSpace(authorMaskedName) ? "Khách hàng" : authorMaskedName);
    }
}
