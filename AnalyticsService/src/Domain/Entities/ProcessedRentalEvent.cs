namespace Dprental.AnalyticsService.Domain.Entities;

/// <summary>Inbox của rental.events.v1: messageId đã xử lý, dùng để bỏ qua khi Kafka giao lại.</summary>
public sealed class ProcessedRentalEvent
{
    public Guid MessageId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
