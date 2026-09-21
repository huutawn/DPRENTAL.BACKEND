using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

/// <summary>Một scope mà service principal được phép xin cho một audience cụ thể.</summary>
[Table("ServicePrincipalScopes", Schema = "auth")]
[Index(nameof(ServicePrincipalId), nameof(Scope), nameof(Audience), IsUnique = true)]
public class ServicePrincipalScope
{
    [Key]
    public Guid Id { get; set; }

    public Guid ServicePrincipalId { get; set; }
    [ForeignKey(nameof(ServicePrincipalId))]
    public ServicePrincipal ServicePrincipal { get; set; } = null!;

    [Required]
    [MaxLength(100)]
    public string Scope { get; set; } = string.Empty;

    /// <summary>Service nào được cấp scope này, ví dụ <c>inventory-api</c>.</summary>
    [Required]
    [MaxLength(100)]
    public string Audience { get; set; } = string.Empty;
}
