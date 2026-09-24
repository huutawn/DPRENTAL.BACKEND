using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

using Dprental.AuthService.Domain.Enums;

/// <summary>Một user thuộc một organization với một role cụ thể trong phạm vi organization đó.</summary>
[Table("OrganizationMemberships", Schema = "auth")]
[Index(nameof(UserId), nameof(OrganizationId), IsUnique = true)]
public class OrganizationMembership
{
    [Key]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    public Guid OrganizationId { get; set; }
    [ForeignKey(nameof(OrganizationId))]
    public Organization Organization { get; set; } = null!;

    /// <summary>Role của user trong phạm vi organization này (khác UserRole, vốn là role toàn cục).</summary>
    public Guid RoleId { get; set; }
    [ForeignKey(nameof(RoleId))]
    public Role Role { get; set; } = null!;

    public MembershipStatus Status { get; set; }

    public DateTime JoinedAtUtc { get; set; }
}
