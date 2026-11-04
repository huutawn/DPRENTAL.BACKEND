using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.Entities;

namespace Dprental.ReviewService.Application.DTOs.Reviews;

/// <summary>
/// DTO phản hồi chi tiết đánh giá đầy đủ (RFC 9110 / Allowlist).
/// Bao gồm đầy đủ thông tin hai trục trạng thái, version và cửa sổ chỉnh sửa.
/// </summary>
/// <param name="ReviewId">Mã định danh đánh giá (UUID).</param>
/// <param name="OrderId">Mã đơn thuê từ RentalService.</param>
/// <param name="ItemId">Mã mục thuê từ RentalService.</param>
/// <param name="Direction">Chiều đánh giá (renter_to_supplier_listing hoặc supplier_to_renter).</param>
/// <param name="AuthorId">Mã người dùng viết đánh giá.</param>
/// <param name="SubjectId">Mã đối tượng nhận đánh giá.</param>
/// <param name="EquipmentListingId">Mã thiết bị liên quan.</param>
/// <param name="Rating">Điểm đánh giá từ 1 đến 5 sao.</param>
/// <param name="Comment">Nội dung bình luận.</param>
/// <param name="AuthorState">Trạng thái của tác giả: Active hoặc Withdrawn.</param>
/// <param name="ModerationState">Trạng thái kiểm duyệt: Visible hoặc Hidden.</param>
/// <param name="Version">Số nguyên phiên bản dùng làm Concurrency Token (Strong ETag).</param>
/// <param name="CreatedAt">Thời điểm tạo đánh giá (UTC).</param>
/// <param name="UpdatedAt">Thời điểm cập nhật gần nhất (UTC).</param>
/// <param name="WithdrawnAt">Thời điểm tác giả rút đánh giá (UTC).</param>
/// <param name="CanEditUntil">Hạn chót cho phép tác giả chỉnh sửa (UTC).</param>
public sealed record ReviewDetailResponse(
    Guid ReviewId,
    Guid OrderId,
    Guid ItemId,
    string Direction,
    Guid AuthorId,
    Guid SubjectId,
    Guid EquipmentListingId,
    int Rating,
    string? Comment,
    string AuthorState,
    string ModerationState,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? WithdrawnAt,
    DateTimeOffset? CanEditUntil)
{
    /// <summary>
    /// Khởi tạo mặc định phục vụ deserialization.
    /// </summary>
    public ReviewDetailResponse()
        : this(
            Guid.Empty,
            Guid.Empty,
            Guid.Empty,
            string.Empty,
            Guid.Empty,
            Guid.Empty,
            Guid.Empty,
            0,
            null,
            string.Empty,
            string.Empty,
            0,
            default,
            null,
            null,
            null)
    {
    }

    /// <summary>
    /// Ánh xạ từ thực thể Domain Review sang ReviewDetailResponse.
    /// </summary>
    /// <param name="review">Thực thể Review.</param>
    /// <returns>Đối tượng ReviewDetailResponse.</returns>
    public static ReviewDetailResponse FromEntity(Review review)
    {
        ArgumentNullException.ThrowIfNull(review);

        return new ReviewDetailResponse(
            review.Id,
            review.OrderId,
            review.ItemId,
            review.Direction == ReviewDirection.supplier_to_renter ? "supplier_to_renter" : "renter_to_supplier_listing",
            review.AuthorId,
            review.SubjectId,
            review.EquipmentListingId,
            review.Rating,
            review.Comment,
            review.AuthorState.ToString(),
            review.ModerationState.ToString(),
            review.Version,
            review.CreatedAt,
            review.UpdatedAt,
            review.WithdrawnAt,
            review.CanEditUntil);
    }
}
