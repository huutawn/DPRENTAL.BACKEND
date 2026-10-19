using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.PaymentService.Domain.Entities.Integrity;

[Table("integrity_stream_manifests")]
public sealed class StreamManifest
{
    [Key]
    [MaxLength(128)]
    public string StreamId { get; private set; } = string.Empty;

    [Required]
    public long LatestVersion { get; private set; }

    [Required]
    [MaxLength(128)]
    public string LatestEntryHash { get; private set; } = string.Empty;

    [Required]
    public Guid LatestEventId { get; private set; }

    public bool IsBlocked { get; private set; }

    [MaxLength(500)]
    public string? BlockedReason { get; private set; }

    [Required]
    public DateTimeOffset UpdatedAt { get; private set; }

    private StreamManifest() { }

    public static StreamManifest Create(
        string streamId,
        long version,
        string entryHash,
        Guid eventId,
        DateTimeOffset updatedAt)
    {
        return new StreamManifest
        {
            StreamId = streamId.Trim(),
            LatestVersion = version,
            LatestEntryHash = entryHash.Trim().ToLowerInvariant(),
            LatestEventId = eventId,
            IsBlocked = false,
            BlockedReason = null,
            UpdatedAt = updatedAt.ToUniversalTime()
        };
    }

    public void Advance(long nextVersion, string nextEntryHash, Guid nextEventId, DateTimeOffset updatedAt)
    {
        LatestVersion = nextVersion;
        LatestEntryHash = nextEntryHash.Trim().ToLowerInvariant();
        LatestEventId = nextEventId;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    public void Block(string reason, DateTimeOffset updatedAt)
    {
        IsBlocked = true;
        BlockedReason = reason.Trim();
        UpdatedAt = updatedAt.ToUniversalTime();
    }
}
