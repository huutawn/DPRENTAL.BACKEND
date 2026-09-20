using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

/// <summary>Tài khoản của một service khác (RentalService, InventoryService, ...) dùng OAuth2 client_credentials.</summary>
[Table("ServicePrincipals", Schema = "auth")]
[Index(nameof(ClientId), IsUnique = true)]
public class ServicePrincipal
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 hexadecimal của client secret; không lưu secret thô.</summary>
    [Required]
    [MaxLength(64)]
    public string SecretHash { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ICollection<ServicePrincipalScope> Scopes { get; set; } = new List<ServicePrincipalScope>();
}
