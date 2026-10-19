using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.PaymentService.IntegrityHost.Entities;

[Table("integrity_entity_heads")]
public sealed class EntityHeadRecord
{
    [Required]
    [MaxLength(64)]
    public string EntityType { get; private set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string EntityId { get; private set; } = string.Empty;

    [Required]
    public Guid LatestAttestationId { get; private set; }

    [Required]
    public long LatestVersion { get; private set; }

    [Required]
    [MaxLength(128)]
    public string LatestEntryHash { get; private set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string LatestDigest { get; private set; } = string.Empty;

    [Required]
    public DateTimeOffset UpdatedAt { get; private set; }

    private EntityHeadRecord() { }

    public static EntityHeadRecord Create(
        string entityType,
        string entityId,
        Guid attestationId,
        long version,
        string entryHash,
        string digest,
        DateTimeOffset updatedAt)
    {
        return new EntityHeadRecord
        {
            EntityType = entityType.Trim(),
            EntityId = entityId.Trim(),
            LatestAttestationId = attestationId,
            LatestVersion = version,
            LatestEntryHash = entryHash.Trim().ToLowerInvariant(),
            LatestDigest = digest.Trim().ToLowerInvariant(),
            UpdatedAt = updatedAt.ToUniversalTime()
        };
    }

    public void Update(Guid attestationId, long version, string entryHash, string digest, DateTimeOffset updatedAt)
    {
        LatestAttestationId = attestationId;
        LatestVersion = version;
        LatestEntryHash = entryHash.Trim().ToLowerInvariant();
        LatestDigest = digest.Trim().ToLowerInvariant();
        UpdatedAt = updatedAt.ToUniversalTime();
    }
}
