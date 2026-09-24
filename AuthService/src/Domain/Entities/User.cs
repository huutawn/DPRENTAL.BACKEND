using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

[Table("Users", Schema = "auth")]
[Index(nameof(Email), IsUnique = true)]
[Index(nameof(CitizenId), IsUnique = true)]
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

    /// <summary>Số CCCD 12 chữ số — dữ liệu cá nhân nhạy cảm, không ghi log và chỉ trả ra ở dạng che.
    /// Null với user tạo qua Google chưa bổ sung hồ sơ.</summary>
    [MaxLength(12)]
    public string? CitizenId { get; set; }

    [MaxLength(500)]
    public string? AvatarUrl { get; set; }

    [MaxLength(15)]
    public string? PhoneNumber { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    /// <summary>Thời điểm user hoàn thiện hồ sơ bắt buộc (CCCD + SĐT + ngày sinh); null khi chưa hoàn thiện.</summary>
    public DateTime? ProfileCompletedAtUtc { get; set; }

    /// <summary>Suy ra từ <see cref="ProfileCompletedAtUtc"/> — không lưu xuống DB.</summary>
    [NotMapped]
    public bool IsProfileCompleted => ProfileCompletedAtUtc is not null;

    public DateTime CreatedAt { get; set; }

    /// <summary>Cho biết tài khoản đã có mật khẩu tự đặt hay chưa (user tạo qua Google có HasPassword = false).</summary>
    public bool HasPassword { get; set; }

    public bool IsActive { get; set; } = true;

    // Dùng cho Abuse Protection (Chống brute-force)
    public int AccessFailedCount { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public ICollection<Session> Sessions { get; set; } = new List<Session>();
    public ICollection<OrganizationMembership> OrganizationMemberships { get; set; } = new List<OrganizationMembership>();
    public ICollection<ExternalLogin> ExternalLogins { get; set; } = new List<ExternalLogin>();

    /// <summary>Các ticket ngắn hạn đã cấp sau khi đăng nhập Google.</summary>
    public ICollection<GoogleLoginTicket> GoogleLoginTickets { get; set; } = new List<GoogleLoginTicket>();
}
