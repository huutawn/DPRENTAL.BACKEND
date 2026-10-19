namespace Dprental.RecommendationService.Domain.Entities;

/// <summary>
/// Lưu trữ raw behavioral event phục vụ kiểm toán, chống trùng lặp và replay dữ liệu.
/// Khóa chính message_id là UUID không nil.
/// </summary>
public sealed class InteractionEvent
{
    public Guid MessageId { get; set; }

    public string MessageType { get; set; } = string.Empty;

    public int Version { get; set; } = 1;

    public string Producer { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public string? CorrelationId { get; set; }

    public Guid? UserId { get; set; }

    public Guid? EquipmentListingId { get; set; }

    public Guid? EquipmentTypeId { get; set; }

    public short ScoreWeight { get; set; }

    public string Envelope { get; set; } = string.Empty;
}
