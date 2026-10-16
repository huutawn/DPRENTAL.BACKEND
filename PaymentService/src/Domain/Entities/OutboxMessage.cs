using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.PaymentService.Domain.Entities;

[Table("outbox_messages")]
public sealed class OutboxMessage
{
    [Key]
    public Guid Id { get; private set; }

    [Required]
    public Guid MessageId { get; private set; }

    [Required]
    [MaxLength(64)]
    public string MessageType { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string AggregateType { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string AggregateId { get; private set; } = string.Empty;

    [Required]
    public string Payload { get; private set; } = string.Empty;

    [Required]
    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }
    private OutboxMessage()
    {
    }

    public static OutboxMessage Create(
        Guid messageId,
        string messageType,
        string aggregateType,
        string aggregateId,
        string payload,
        DateTimeOffset createdAt)
    {
        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            MessageType = messageType,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            Payload = payload,
            CreatedAt = createdAt.ToUniversalTime(),
            PublishedAt = null
        };
    }

    public void MarkPublished(DateTimeOffset publishedAt)
    {
        if (PublishedAt is not null) return;
        PublishedAt = publishedAt.ToUniversalTime();
    }
}
