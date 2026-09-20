using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

/// <summary>Mã quyền ổn định dùng trong policy, không đổi khi role đổi tên.</summary>
[Table("Permissions", Schema = "auth")]
[Index(nameof(Code), IsUnique = true)]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Permission là danh từ nghiệp vụ, không phải CAS permission.")]
public class Permission
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>Mã ổn định dùng trong <c>RequireClaim("perm", Code)</c>, ví dụ <c>roles.assign</c>.</summary>
    [Required]
    [MaxLength(100)]
    public string Code { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}
