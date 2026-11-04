namespace Dprental.ReviewService.Application.DTOs.Reviews;

/// <summary>
/// DTO phản hồi tổng hợp điểm đánh giá cho thiết bị hoặc nhà cung cấp.
/// </summary>
/// <param name="TargetType">Loại đối tượng ('listing' hoặc 'supplier').</param>
/// <param name="TargetId">Mã định danh đối tượng (UUID).</param>
/// <param name="AverageRating">Điểm trung bình (làm tròn 1 chữ số thập phân), null khi chưa có đánh giá nào.</param>
/// <param name="ReviewCount">Tổng số lượng đánh giá hợp lệ đang hiển thị.</param>
/// <param name="RatingDistribution">Phân bổ số lượng đánh giá theo số sao từ 1 đến 5.</param>
public sealed record ReviewSummaryResponse(
    string TargetType,
    Guid TargetId,
    double? AverageRating,
    int ReviewCount,
    IReadOnlyDictionary<string, int> RatingDistribution)
{
    /// <summary>
    /// Khởi tạo mặc định phục vụ deserialization.
    /// </summary>
    public ReviewSummaryResponse()
        : this(
            string.Empty,
            Guid.Empty,
            null,
            0,
            CreateEmptyDistribution())
    {
    }

    /// <summary>
    /// Tạo mới một bản tổng hợp rỗng khi đối tượng chưa có đánh giá nào.
    /// </summary>
    public static ReviewSummaryResponse Empty(string targetType, Guid targetId)
    {
        return new ReviewSummaryResponse(
            targetType,
            targetId,
            null,
            0,
            CreateEmptyDistribution());
    }

    /// <summary>
    /// Khởi tạo phân bổ mặc định 1..5 sao với số lượng 0, theo thứ tự từ 5 xuống 1.
    /// </summary>
    public static Dictionary<string, int> CreateEmptyDistribution()
    {
        return new Dictionary<string, int>
        {
            ["5"] = 0,
            ["4"] = 0,
            ["3"] = 0,
            ["2"] = 0,
            ["1"] = 0
        };
    }
}
