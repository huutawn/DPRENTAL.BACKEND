namespace Dprental.RecommendationService.Domain.Entities;

/// <summary>
/// Bảng projection lưu điểm phổ biến toàn cầu của từng equipment_listing_id.
/// Khóa chính equipment_listing_id.
/// </summary>
public sealed class ListingPopularity
{
    public Guid EquipmentListingId { get; set; }

    public long Score { get; set; }

    public DateTimeOffset LastOccurredAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
