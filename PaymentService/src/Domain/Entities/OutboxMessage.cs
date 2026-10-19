namespace Dprental.PaymentService.Domain.Entities;

public sealed class OutboxMessage
{
    public Guid Id { get; private set; }
    public Guid MessageId { get; private set; }
    public string MessageType { get; private set; } = string.Empty;
    public string AggregateType { get; private set; } = string.Empty;
    public string AggregateId { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public string? LockedBy { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }
    public int RetryCount { get; private set; }
    public DateTimeOffset? NextRetryAt { get; private set; }
    public string? LastErrorMessage { get; private set; }
    public Guid? EventId { get; private set; }
    public Guid? OperationId { get; private set; }

    private OutboxMessage()
    {
    }

    public static OutboxMessage Create(
        Guid messageId,
        string messageType,
        string aggregateType,
        string aggregateId,
        string payload,
        DateTimeOffset createdAt,
        Guid? eventId = null,
        Guid? operationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateType);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            MessageType = messageType.Trim(),
            AggregateType = aggregateType.Trim(),
            AggregateId = aggregateId.Trim(),
            Payload = payload,
            CreatedAt = createdAt.ToUniversalTime(),
            PublishedAt = null,
            LockedBy = null,
            LockedUntilUtc = null,
            RetryCount = 0,
            NextRetryAt = null,
            LastErrorMessage = null,
            EventId = eventId,
            OperationId = operationId
        };
    }

    public void LinkAttestation(Guid eventId, Guid? operationId = null)
    {
        EventId = eventId;
        if (operationId.HasValue)
        {
            OperationId = operationId;
        }
    }

    public void Claim(string workerId, DateTimeOffset lockedUntilUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        LockedBy = workerId;
        LockedUntilUtc = lockedUntilUtc.ToUniversalTime();
        RetryCount++;
    }

    public void ReleaseClaim()
    {
        LockedBy = null;
        LockedUntilUtc = null;
    }

    public void MarkPublished(DateTimeOffset publishedAt)
    {
        PublishedAt = publishedAt.ToUniversalTime();
        LockedBy = null;
        LockedUntilUtc = null;
    }

    public void RecordAttemptFailure(string errorMessage, DateTimeOffset attemptedAt, DateTimeOffset nextRetryAt)
    {
        LastErrorMessage = errorMessage;
        NextRetryAt = nextRetryAt.ToUniversalTime();
        LockedBy = null;
        LockedUntilUtc = null;
    }

    public void RecordExhausted(string errorMessage, DateTimeOffset attemptedAt)
    {
        LastErrorMessage = errorMessage;
        LockedBy = null;
        LockedUntilUtc = null;
    }
}
