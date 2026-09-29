using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

[Table("MediaOutboxMessages", Schema = "auth")]
[Index(nameof(Status), nameof(NextRetryAtUtc))]
[Index(nameof(MediaId))]
public class MediaOutboxMessage
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid MediaId { get; set; }

    [Required]
    [MaxLength(50)]
    public string Visibility { get; set; } = "public";

    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = "Pending";

    public int RetryCount { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? ProcessedAtUtc { get; set; }

    public DateTime? NextRetryAtUtc { get; set; }
}
