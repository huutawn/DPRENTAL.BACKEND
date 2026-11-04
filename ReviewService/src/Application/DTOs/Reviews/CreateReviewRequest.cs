namespace Dprental.ReviewService.Application.DTOs.Reviews;

/// <summary>
/// Yêu cầu tạo mới đánh giá người dùng (Allowlist DTO).
/// Tuân thủ Zero Trust Client Input: không nhận authorId, subjectId, targetId từ client.
/// </summary>
/// <param name="OrderId">Mã đơn thuê từ RentalService.</param>
/// <param name="ItemId">Mã mục thuê từ RentalService.</param>
/// <param name="Rating">Điểm đánh giá từ 1 đến 5 sao.</param>
/// <param name="Comment">Nội dung bình luận (tối đa 1.000 ký tự Unicode scalar values).</param>
public sealed record CreateReviewRequest(
    Guid OrderId,
    Guid ItemId,
    int Rating,
    string? Comment = null)
{
    /// <summary>
    /// Khởi tạo mặc định phục vụ deserialization.
    /// </summary>
    public CreateReviewRequest()
        : this(Guid.Empty, Guid.Empty, 0, null)
    {
    }
}
