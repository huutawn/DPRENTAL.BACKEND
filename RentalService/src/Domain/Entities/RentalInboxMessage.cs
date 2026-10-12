using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("rental_inbox_messages", Schema = "rental")]
[Index(nameof(MessageId), IsUnique = true)]
[Index(nameof(Status))]
[Index(nameof(Status), nameof(NextRetryAt), nameof(ReceivedAt))]
public class RentalInboxMessage
{
    public const string StatusReceived = "Received";
    public const string StatusFailed = "Failed";
    public const string StatusProcessed = "Processed";
    public const string StatusDeadLettered = "DeadLettered";

    [Key]
    public Guid Id { get; private set; }

    public Guid MessageId { get; private set; }

    [Required]
    [MaxLength(128)]
    public string MessageType { get; private set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string Producer { get; private set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string AggregateId { get; private set; } = string.Empty;

    [MaxLength(128)]
    public string? CorrelationId { get; private set; }

    public Guid? CausationId { get; private set; }

    [Required]
    public string Payload { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string PayloadFingerprint { get; private set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string Status { get; private set; } = StatusReceived;

    public int RetryCount { get; private set; }

    public DateTimeOffset? NextRetryAt { get; private set; }

    [MaxLength(2000)]
    public string? ErrorMessage { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    [MaxLength(256)]
    public string? KafkaTopic { get; private set; }

    public int? KafkaPartition { get; private set; }
    public long? KafkaOffset { get; private set; }

    private RentalInboxMessage()
    {
    }

    public RentalInboxMessage(
        Guid id,
        Guid messageId,
        string messageType,
        string producer,
        string aggregateId,
        string? correlationId,
        Guid? causationId,
        string payload,
        DateTimeOffset receivedAt,
        string? payloadFingerprint = null,
        string? kafkaTopic = null,
        int? kafkaPartition = null,
        long? kafkaOffset = null)
    {
        MessageId = messageId;
        MessageType = messageType;
        Producer = producer;
        AggregateId = aggregateId;
        CorrelationId = correlationId;
        CausationId = causationId;
        Payload = payload;
        PayloadFingerprint = payloadFingerprint ?? ComputeHash(payload);
        ReceivedAt = receivedAt;
        Status = StatusReceived;
        KafkaTopic = kafkaTopic;
        KafkaPartition = kafkaPartition;
        KafkaOffset = kafkaOffset;
    }
    public void MarkProcessed(DateTimeOffset processedAt)
    {
        Status = StatusProcessed;
        ProcessedAt = processedAt;
    }

    public void MarkFailed(string errorMessage, DateTimeOffset? nextRetryAt = null)
    {
        RetryCount++;
        Status = StatusFailed;
        ErrorMessage = errorMessage;
        NextRetryAt = nextRetryAt;
    }

    public void MarkDeadLettered(string errorMessage)
    {
        Status = StatusDeadLettered;
        ErrorMessage = errorMessage;
    }

    private static string ComputeHash(string input)
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
