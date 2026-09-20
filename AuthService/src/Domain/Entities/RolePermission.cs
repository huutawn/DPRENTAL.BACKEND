using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

[Table("RolePermissions", Schema = "auth")]
[PrimaryKey(nameof(RoleId), nameof(PermissionId))] // Khóa chính kép
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Permission là danh từ nghiệp vụ, không phải CAS permission.")]
public class RolePermission
{
    public Guid RoleId { get; set; }
    [ForeignKey(nameof(RoleId))]
    public Role Role { get; set; } = null!;

    public Guid PermissionId { get; set; }
    [ForeignKey(nameof(PermissionId))]
    public Permission Permission { get; set; } = null!;
}
