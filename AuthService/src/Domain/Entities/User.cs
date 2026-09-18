using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Dprental.AuthService.Domain.Entities;

[Table("Users", Schema = "auth")]
[Index(nameof(Email), IsUnique = true)]
public class User
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string PasswordHash { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string FullName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }

    /// <summary>Các vai trò hiện có của người dùng.</summary>
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

    /// <summary>Các phiên đăng nhập được lưu bền của người dùng.</summary>
    public ICollection<Session> Sessions { get; set; } = new List<Session>();

    public int AccessFailedCount { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }

    public string? PasswordResetToken { get; set; }
    public DateTimeOffset? ResetTokenExpiry { get; set; }

}
