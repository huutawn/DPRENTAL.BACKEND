using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

[Table("RecoveryTokens", Schema = "auth")]
[Index(nameof(TokenHash), IsUnique = true)]
public class RecoveryToken
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid UserId { get; set; }

    [Required]
    [MaxLength(256)]
    public string TokenHash { get; set; } = string.Empty; // Tuyệt đối không lưu raw token

    [Required]
    [MaxLength(50)]
    public string Purpose { get; set; } = "ResetPassword"; // Dùng để mở rộng sau này (vd: EmailVerification)

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? UsedAtUtc { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;
}