using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

/// <summary>Phiên đăng nhập bền, là phạm vi thu hồi của một refresh token family.</summary>
[Table("Sessions", Schema = "auth")]
[Index(nameof(UserId))]
public class Session
{
    /// <summary>Định danh duy nhất của session.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>Người dùng sở hữu session.</summary>
    public Guid UserId { get; set; }

    /// <summary>Thông tin người dùng sở hữu session.</summary>
    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    /// <summary>Thời điểm session được tạo theo UTC.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Hạn tuyệt đối của session theo UTC; refresh không gia hạn giá trị này.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Thời điểm session bị thu hồi; null nếu session còn hiệu lực.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Các refresh token đã được phát hành trong session này.</summary>
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();

    public string? ReplacedByToken { get; set; }
    public string? DeviceName { get; set; }
    public string? IpAddress { get; set; }
}
