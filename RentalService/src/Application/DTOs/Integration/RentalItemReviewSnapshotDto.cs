namespace Dprental.RentalService.Application.DTOs.Integration;

/// <summary>
/// DTO chứa snapshot thông tin mục thuê phục vụ kiểm tra điều kiện đánh giá từ ReviewService.
/// Tuân thủ nguyên tắc Data Minimization (loại bỏ orderCode).
/// </summary>
public sealed record RentalItemReviewSnapshotDto
{
    public Guid OrderId { get; init; }
    public Guid ItemId { get; init; }
    public Guid RenterId { get; init; }
    public Guid SupplierId { get; init; }
    public Guid EquipmentListingId { get; init; }
    public Guid ListingId => EquipmentListingId;
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset? CompletedAt { get; init; }
    public long OrderVersion { get; init; }

    public RentalItemReviewSnapshotDto() { }

    public RentalItemReviewSnapshotDto(
        Guid orderId,
        Guid itemId,
        Guid renterId,
        Guid supplierId,
        Guid equipmentListingId,
        string status,
        DateTimeOffset? completedAt,
        long orderVersion)
    {
        OrderId = orderId;
        ItemId = itemId;
        RenterId = renterId;
        SupplierId = supplierId;
        EquipmentListingId = equipmentListingId;
        Status = status;
        CompletedAt = completedAt;
        OrderVersion = orderVersion;
    }
}
