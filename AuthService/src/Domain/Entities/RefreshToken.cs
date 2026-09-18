using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DpRent.Domain.Entities;

[Table("RefreshTokens", Schema = "auth")]
public class RefreshToken
{
    [Key]
    public Guid Id { get; set; }

    // Store only a server-side hash of the refresh token. The raw token is returned to the client
    // but never persisted. We keep the column name as 'Token' for compatibility with existing
    // migrations/schema by mapping the property to that column.
    [Required]
    [MaxLength(200)]
    [Column("Token")]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiryDate { get; set; }
    public bool IsRevoked { get; set; }
    public DateTime CreatedAt { get; set; }

    public Guid UserId { get; set; }
    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    // Rotation and reuse-detection fields
    // Hash of the token that replaced this one (if rotated)
    [MaxLength(200)]
    public string? ReplacedByTokenHash { get; set; }

    // When this token was revoked (either rotated or explicitly revoked)
    public DateTime? RevokedAt { get; set; }

    // Optional reason for revocation (e.g., "Rotated", "ReuseDetected", "ManualRevocation")
    [MaxLength(200)]
    public string? RevokedReason { get; set; }
}