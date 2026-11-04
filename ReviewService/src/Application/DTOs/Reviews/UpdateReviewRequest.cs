namespace Dprental.ReviewService.Application.DTOs.Reviews;

/// <summary>
/// Yêu cầu chỉnh sửa nội dung hoặc điểm đánh giá đã tồn tại.
/// </summary>
/// <param name="Rating">Điểm đánh giá cập nhật (1 đến 5 sao).</param>
/// <param name="Comment">Nội dung bình luận cập nhật (tối đa 1.000 ký tự Unicode scalar values).</param>
public sealed record UpdateReviewRequest(
    int Rating,
    string? Comment = null)
{
    /// <summary>
    /// Khởi tạo mặc định phục vụ deserialization.
    /// </summary>
    public UpdateReviewRequest()
        : this(0, null)
    {
    }
}
