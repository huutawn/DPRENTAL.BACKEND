namespace Dprental.ReviewService.Application.Kafka.Contracts;

/// <summary>
/// Dữ liệu chi tiết sự kiện RentalItemCompleted phát ra từ Transactional Outbox của RentalService.
/// </summary>
public sealed record RentalItemCompletedPayload(
    Guid UserId,
    Guid EquipmentListingId,
    Guid? EquipmentTypeId,
    Guid RentalOrderId,
    Guid RentalItemId,
    long OrderVersion,
    DateTimeOffset CompletedAt);
