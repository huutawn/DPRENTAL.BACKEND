using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
namespace Dprental.AuthService.Domain.Entities;

/// <summary>Refresh token: lưu hash server-side, thuộc về một user cụ thể.</summary>
[Table("RefreshTokens", Schema = "auth")]
[Index(nameof(TokenHash), IsUnique = true)]
[Index(nameof(UserId))]
public class RefreshToken
{
    /// <summary>Định danh nội bộ của refresh token.</summary>
    [Key]
    public Guid Id { get; set; }

    // Store only a server-side hash of the refresh token. The raw token is returned to the client
    // but never persisted.
    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Thời điểm phát hành token.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Thời điểm phát hành token theo UTC cho session flow.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Thời điểm token hết hạn.</summary>
    public DateTime ExpiryDate { get; set; }

    /// <summary>Thời điểm hết hạn theo UTC cho session flow.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Token đã bị thu hồi (bởi rotation hoặc thủ công).</summary>
    public bool IsRevoked { get; set; }

    /// <summary>Thời điểm token đã được sử dụng để xoay vòng.</summary>
    public DateTime? UsedAtUtc { get; set; }

    /// <summary>Thời điểm token bị thu hồi.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>Thời điểm thu hồi theo UTC cho session flow.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Lý do thu hồi (tuỳ chọn).</summary>
    [MaxLength(200)]
    public string? RevokedReason { get; set; }

    // Rotation / reuse-detection fields
    /// <summary>Hash của token đã thay thế token này (nếu có).</summary>
    [MaxLength(200)]
    public string? ReplacedByTokenHash { get; set; }

    public Guid UserId { get; set; }
    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    public Guid SessionId { get; set; }

    [ForeignKey(nameof(SessionId))]
    public Session Session { get; set; } = null!;
}
