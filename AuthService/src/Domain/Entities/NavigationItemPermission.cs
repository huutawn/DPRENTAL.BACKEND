using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

/// <summary>Permission hiện có cần có để menu item được hiển thị.</summary>
[Table("NavigationItemPermissions", Schema = "auth")]
[PrimaryKey(nameof(NavigationItemId), nameof(PermissionId))]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Permission là danh từ nghiệp vụ, không phải CAS permission.")]
public class NavigationItemPermission
{
    public Guid NavigationItemId { get; set; }

    [ForeignKey(nameof(NavigationItemId))]
    public NavigationItem NavigationItem { get; set; } = null!;

    public Guid PermissionId { get; set; }

    [ForeignKey(nameof(PermissionId))]
    public Permission Permission { get; set; } = null!;
}
