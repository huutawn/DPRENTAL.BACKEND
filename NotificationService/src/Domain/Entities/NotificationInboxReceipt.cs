namespace Dprental.NotificationService.Domain.Entities;

/// <summary>
/// Bản ghi idempotency cho Kafka consumer lưu trữ tại notification.inbox_receipts.
/// </summary>
public sealed class NotificationInboxReceipt
{
    public Guid Id { get; set; }
    public string ConsumerName { get; set; } = string.Empty;
    public Guid MessageId { get; set; }
    public string Producer { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // Processed, Ignored, Quarantined
    public DateTimeOffset ProcessedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDetail { get; set; }
}
