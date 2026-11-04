namespace Dprental.ReviewService.Domain.Entities;

/// <summary>
/// Lưu trữ các sự kiện nghiệp vụ ReviewService phát hành bất đồng bộ theo mô hình Transactional Outbox.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; private set; }
    public string Topic { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public string PartitionKey { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public string? ClaimOwner { get; private set; }
    public string? LastErrorCode { get; private set; }
    public string DeliveryState { get; private set; } = "Pending";
    private OutboxMessage() { }

    public OutboxMessage(
        Guid id,
        string topic,
        string eventType,
        string partitionKey,
        string payload,
        DateTimeOffset createdAt)
    {
        Id = id;
        Topic = topic;
        EventType = eventType;
        PartitionKey = partitionKey;
        Payload = payload;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public void Claim(string owner, DateTimeOffset leaseExpiry)
    {
        ClaimOwner = owner;
        LeaseExpiresAt = leaseExpiry.ToUniversalTime();
    }

    public void FailAttempt(string errorCode, DateTimeOffset nextAttempt, bool park = false)
    {
        Attempts++;
        LastErrorCode = errorCode;
        ClaimOwner = null;
        LeaseExpiresAt = null;
        NextAttemptAt = nextAttempt.ToUniversalTime();
        if (park || Attempts >= 5)
        {
            DeliveryState = "Parked";
        }
    }

    public void MarkProcessed(DateTimeOffset processedAt)
    {
        ProcessedAt = processedAt.ToUniversalTime();
        DeliveryState = "Published";
        ClaimOwner = null;
        LeaseExpiresAt = null;
    }
}
