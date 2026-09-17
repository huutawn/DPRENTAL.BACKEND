using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DpRent.Domain.Entities;

[Table("UserRoles", Schema = "auth")]
[PrimaryKey(nameof(UserId), nameof(RoleId))] // Khóa chính kép
public class UserRole
{
    public Guid UserId { get; set; }
    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    public Guid RoleId { get; set; }
    [ForeignKey(nameof(RoleId))]
    public Role Role { get; set; } = null!;
}
