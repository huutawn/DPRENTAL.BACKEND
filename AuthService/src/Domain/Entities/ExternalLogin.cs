using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

/// <summary>Liên kết một tài khoản ở nhà cung cấp bên ngoài (ví dụ Google) với một User của hệ thống.</summary>
[Table("ExternalLogins", Schema = "auth")]
[Index(nameof(Provider), nameof(ProviderSubject), IsUnique = true)]
[Index(nameof(UserId), nameof(Provider), IsUnique = true)]
public class ExternalLogin
{
    [Key]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    /// <summary>Tên nhà cung cấp, ví dụ <c>Google</c>.</summary>
    [Required]
    [MaxLength(50)]
    public string Provider { get; set; } = string.Empty;

    /// <summary>Id ổn định của tài khoản ở nhà cung cấp (claim <c>sub</c>); dùng để tra cứu thay vì email vì email có thể đổi.</summary>
    [Required]
    [MaxLength(255)]
    public string ProviderSubject { get; set; } = string.Empty;

    /// <summary>Email lúc liên kết, chỉ để tham khảo và audit.</summary>
    [MaxLength(320)]
    public string? Email { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
