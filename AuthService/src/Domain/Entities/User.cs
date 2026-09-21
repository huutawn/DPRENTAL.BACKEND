using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

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

    public bool IsActive { get; set; } = true;

    // Dùng cho Abuse Protection (Chống brute-force)
    public int AccessFailedCount { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public ICollection<Session> Sessions { get; set; } = new List<Session>();
    public ICollection<OrganizationMembership> OrganizationMemberships { get; set; } = new List<OrganizationMembership>();
    public ICollection<ExternalLogin> ExternalLogins { get; set; } = new List<ExternalLogin>();

    public ICollection<RecoveryToken> RecoveryTokens { get; set; } = new List<RecoveryToken>();
}