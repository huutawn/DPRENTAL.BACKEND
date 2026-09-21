using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

/// <summary>
/// Ticket opaque, ngắn hạn và một lần dùng để Next BFF đổi kết quả đăng nhập Google lấy token pair.
/// Chỉ hash ticket được lưu bền; ticket thô không được persistence hay ghi log.
/// </summary>
[Table("GoogleLoginTickets", Schema = "auth")]
[Index(nameof(TicketHash), IsUnique = true)]
[Index(nameof(ExpiresAtUtc))]
public sealed class GoogleLoginTicket
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string TicketHash { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Thời điểm ticket được consume; null nghĩa là chưa dùng.</summary>
    public DateTime? UsedAtUtc { get; set; }
}
