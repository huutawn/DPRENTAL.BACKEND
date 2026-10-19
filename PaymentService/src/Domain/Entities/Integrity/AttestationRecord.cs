using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.PaymentService.Domain.Entities.Integrity;

[Table("integrity_attestations")]
public sealed class AttestationRecord
{
    [Key]
    public Guid Id { get; private set; }

    [Required]
    [MaxLength(128)]
    public string StreamId { get; private set; } = string.Empty;

    [Required]
    public long Version { get; private set; }

    [Required]
    public Guid EventId { get; private set; }

    [Required]
    [MaxLength(128)]
    public string EntryHash { get; private set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string PayloadHash { get; private set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string PreviousHash { get; private set; } = string.Empty;

    [Required]
    [MaxLength(512)]
    public string Signature { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string KeyId { get; private set; } = string.Empty;

    [Required]
    public DateTimeOffset ConfirmedAt { get; private set; }

    private AttestationRecord() { }

    public static AttestationRecord Create(
        string streamId,
        long version,
        Guid eventId,
        string entryHash,
        string payloadHash,
        string previousHash,
        string signature,
        string keyId,
        DateTimeOffset confirmedAt)
    {
        return new AttestationRecord
        {
            Id = Guid.NewGuid(),
            StreamId = streamId.Trim(),
            Version = version,
            EventId = eventId,
            EntryHash = entryHash.Trim().ToLowerInvariant(),
            PayloadHash = payloadHash.Trim().ToLowerInvariant(),
            PreviousHash = previousHash.Trim().ToLowerInvariant(),
            Signature = signature.Trim(),
            KeyId = keyId.Trim(),
            ConfirmedAt = confirmedAt.ToUniversalTime()
        };
    }
}
