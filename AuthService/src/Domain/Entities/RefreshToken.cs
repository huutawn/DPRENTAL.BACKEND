using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

/// <summary>Refresh token chỉ lưu hash, thuộc về một session cụ thể.</summary>
[Table("RefreshTokens", Schema = "auth")]
[Index(nameof(TokenHash), IsUnique = true)]
[Index(nameof(SessionId))]
public class RefreshToken
{
    /// <summary>Định danh nội bộ của refresh token.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// SHA-256 dạng hexadecimal của giá trị token client nhận được.
    /// Không lưu token thô trong cơ sở dữ liệu.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Thời điểm phát hành token theo UTC.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Thời điểm token hết hạn theo UTC, luôn bằng hạn tuyệt đối của session.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Thời điểm token đã được dùng để đổi lấy token mới; null khi token còn dùng được.</summary>
    public DateTime? UsedAtUtc { get; set; }

    /// <summary>Thời điểm token bị thu hồi trực tiếp; null khi chưa bị thu hồi.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Session sở hữu token này.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Thông tin session sở hữu token.</summary>
    [ForeignKey(nameof(SessionId))]
    public Session Session { get; set; } = null!;
}
