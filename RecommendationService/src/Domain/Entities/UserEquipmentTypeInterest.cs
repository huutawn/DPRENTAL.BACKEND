namespace Dprental.RecommendationService.Domain.Entities;

/// <summary>
/// Bảng projection lưu điểm quan tâm theo equipment_type_id của từng user.
/// Khóa chính tổng hợp (user_id, equipment_type_id).
/// </summary>
public sealed class UserEquipmentTypeInterest
{
    public Guid UserId { get; set; }

    public Guid EquipmentTypeId { get; set; }

    public long Score { get; set; }

    public DateTimeOffset LastOccurredAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
