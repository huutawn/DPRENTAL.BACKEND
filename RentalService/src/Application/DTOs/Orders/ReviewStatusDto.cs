namespace Dprental.RentalService.Application.DTOs.Orders;

/// <summary>
/// Trạng thái đánh giá cho từng dòng thiết bị trong đơn hàng.
/// </summary>
/// <param name="State">Trạng thái đánh giá: AVAILABLE, REVIEWED, EXPIRED, NOT_ELIGIBLE, UNKNOWN.</param>
/// <param name="ReviewId">Mã định danh đánh giá đã tạo (chỉ có khi state là REVIEWED).</param>
/// <param name="DeadlineAt">Hạn chót gửi đánh giá (chỉ có khi trạng thái còn thời hạn hoặc đã hết hạn).</param>
public sealed record ReviewStatusDto(
    string State,
    Guid? ReviewId,
    DateTimeOffset? DeadlineAt)
{
    /// <summary>
    /// Trạng thái mặc định khi chưa xác định hoặc downstream gặp lỗi.
    /// </summary>
    public static readonly ReviewStatusDto Unknown = new("UNKNOWN", null, null);
}
