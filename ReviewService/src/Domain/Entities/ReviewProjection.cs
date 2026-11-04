namespace Dprental.ReviewService.Domain.Entities;

/// <summary>
/// Bảng chiếu (read model projection) lưu trữ thông tin điều kiện đánh giá của các mục thuê đã hoàn tất.
/// </summary>
public sealed class ReviewProjection
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ItemId { get; private set; }
    public Guid RenterId { get; private set; }
    public Guid SupplierId { get; private set; }
    public Guid EquipmentListingId { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? DeadlineAt { get; private set; }
    public bool IsEligible { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private ReviewProjection() { }

    public ReviewProjection(
        Guid id,
        Guid orderId,
        Guid itemId,
        Guid renterId,
        Guid supplierId,
        Guid equipmentListingId,
        DateTimeOffset? completedAt,
        DateTimeOffset? deadlineAt,
        bool isEligible,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrderId = orderId;
        ItemId = itemId;
        RenterId = renterId;
        SupplierId = supplierId;
        EquipmentListingId = equipmentListingId;
        CompletedAt = completedAt?.ToUniversalTime();
        DeadlineAt = deadlineAt?.ToUniversalTime();
        IsEligible = isEligible;
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = createdAt.ToUniversalTime();
    }

    public void UpdateEligibility(
        Guid supplierId,
        DateTimeOffset completedAt,
        DateTimeOffset deadlineAt,
        bool isEligible,
        DateTimeOffset updatedAt)
    {
        SupplierId = supplierId;
        CompletedAt = completedAt.ToUniversalTime();
        DeadlineAt = deadlineAt.ToUniversalTime();
        IsEligible = isEligible;
        UpdatedAt = updatedAt.ToUniversalTime();
    }
}
