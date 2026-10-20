using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("rental_outbox_messages", Schema = "rental")]
[Index(nameof(Status), nameof(ScheduledAt), nameof(CreatedAt))]
[Index(nameof(Status), nameof(ScheduledAt), nameof(LockedUntilUtc))]
[Index(nameof(Status))]
[Index(nameof(Topic), nameof(PartitionKey), nameof(Sequence), nameof(Status))]
public class RentalOutboxMessage
{
    [Key]
    public Guid Id { get; private set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Sequence { get; private set; }

    [Required]
    [MaxLength(256)]
    public string Topic { get; private set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string PartitionKey { get; private set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string MessageType { get; private set; } = string.Empty;

    [Required]
    public string Payload { get; private set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string Status { get; private set; } = "Pending";

    public int RetryCount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ScheduledAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public DateTimeOffset? LastAttemptedAt { get; private set; }

    [MaxLength(128)]
    public string? LockedBy { get; private set; }

    public DateTimeOffset? LockedUntilUtc { get; private set; }

    [MaxLength(2000)]
    public string? ErrorMessage { get; private set; }
    private RentalOutboxMessage()
    {
    }

    public RentalOutboxMessage(
        Guid id,
        string topic,
        string partitionKey,
        string messageType,
        string payload,
        DateTimeOffset createdAt,
        DateTimeOffset? scheduledAt = null)
    {
        Id = id;
        Topic = topic;
        PartitionKey = partitionKey;
        MessageType = messageType;
        Payload = payload;
        CreatedAt = createdAt;
        ScheduledAt = scheduledAt;
        Status = "Pending";
    }

    public void Claim(string workerId, DateTimeOffset lockedUntilUtc)
    {
        LockedBy = workerId;
        LockedUntilUtc = lockedUntilUtc;
    }

    public void ReleaseLock()
    {
        LockedBy = null;
        LockedUntilUtc = null;
    }

    public void MarkPublished(DateTimeOffset publishedAt)
    {
        Status = "Published";
        PublishedAt = publishedAt;
        LockedBy = null;
        LockedUntilUtc = null;
    }

    public void RecordFailure(string errorMessage, DateTimeOffset attemptedAt, DateTimeOffset? nextScheduledAt = null, string? targetStatus = null)
    {
        RetryCount++;
        LastAttemptedAt = attemptedAt;
        ErrorMessage = errorMessage;
        ScheduledAt = nextScheduledAt;
        LockedBy = null;
        LockedUntilUtc = null;
        if (!string.IsNullOrWhiteSpace(targetStatus))
        {
            Status = targetStatus;
            if (targetStatus == "DeadLettered")
            {
                ScheduledAt = null;
            }
        }
        else if (RetryCount >= 10)
        {
            Status = "DeadLettered";
            ScheduledAt = null;
        }
        else
        {
            Status = "Pending";
        }
    }

    public void ResetForReplay(DateTimeOffset scheduledAt)
    {
        Status = "Pending";
        ScheduledAt = scheduledAt;
        PublishedAt = null;
        LockedBy = null;
        LockedUntilUtc = null;
        ErrorMessage = null;
    }
}
