using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.PaymentService.Domain.Entities.Integrity;

[Table("integrity_incidents")]
public sealed class IntegrityIncident
{
    [Key]
    public Guid Id { get; private set; }

    [Required]
    [MaxLength(64)]
    public string IncidentType { get; private set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string StreamId { get; private set; } = string.Empty;

    public long? Version { get; private set; }

    public Guid? EventId { get; private set; }

    [Required]
    [MaxLength(2000)]
    public string Details { get; private set; } = string.Empty;

    [Required]
    public DateTimeOffset DetectedAt { get; private set; }

    [Required]
    [MaxLength(32)]
    public string Status { get; private set; } = "OPEN";

    private IntegrityIncident() { }

    public static IntegrityIncident Create(
        string incidentType,
        string streamId,
        long? version,
        Guid? eventId,
        string details,
        DateTimeOffset detectedAt)
    {
        return new IntegrityIncident
        {
            Id = Guid.NewGuid(),
            IncidentType = incidentType.Trim(),
            StreamId = streamId.Trim(),
            Version = version,
            EventId = eventId,
            Details = details.Trim(),
            DetectedAt = detectedAt.ToUniversalTime(),
            Status = "OPEN"
        };
    }
}
