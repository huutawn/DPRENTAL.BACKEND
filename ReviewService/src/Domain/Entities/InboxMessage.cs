namespace Dprental.ReviewService.Domain.Entities;

/// <summary>
/// Bảng lưu trữ tin nhắn Kafka đã tiếp nhận để thực hiện deduplication theo nguyên tắc Transactional Inbox.
/// </summary>
public sealed class InboxMessage
{
    public Guid MessageId { get; private set; }
    public string ConsumerGroup { get; private set; } = string.Empty;
    public string MessageType { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset ProcessedAt { get; private set; }

    private InboxMessage() { }

    public InboxMessage(
        Guid messageId,
        string consumerGroup,
        string messageType,
        DateTimeOffset occurredAt,
        DateTimeOffset processedAt)
    {
        MessageId = messageId;
        ConsumerGroup = consumerGroup ?? string.Empty;
        MessageType = messageType ?? string.Empty;
        OccurredAt = occurredAt.ToUniversalTime();
        ProcessedAt = processedAt.ToUniversalTime();
    }
}
