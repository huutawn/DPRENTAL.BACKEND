namespace Dprental.ReviewService.Application.DTOs.Rental;

/// <summary>
/// DTO chứa snapshot thông tin mục thuê phục vụ kiểm tra điều kiện đánh giá từ RentalService.
/// Tuân thủ nguyên tắc Data Minimization (loại bỏ orderCode).
/// </summary>
/// <param name="OrderId">Mã đơn thuê.</param>
/// <param name="ItemId">Mã mục thuê.</param>
/// <param name="RenterId">Mã người thuê (người khởi tạo đơn).</param>
/// <param name="SupplierId">Mã nhà cung cấp thiết bị.</param>
/// <param name="EquipmentListingId">Mã bài đăng thiết bị.</param>
/// <param name="Status">Trạng thái mục thuê (ví dụ: Completed).</param>
/// <param name="CompletedAt">Thời điểm hoàn tất mục thuê (UTC).</param>
/// <param name="OrderVersion">Phiên bản đơn thuê tại RentalService.</param>
public sealed record RentalItemReviewSnapshotDto(
    Guid OrderId,
    Guid ItemId,
    Guid RenterId,
    Guid SupplierId,
    Guid EquipmentListingId,
    string Status,
    DateTimeOffset? CompletedAt,
    long OrderVersion)
{
    /// <summary>
    /// Khởi tạo mặc định phục vụ deserialization.
    /// </summary>
    public RentalItemReviewSnapshotDto()
        : this(Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, string.Empty, null, 0)
    {
    }
}
