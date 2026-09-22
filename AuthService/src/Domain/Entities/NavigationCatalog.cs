using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.AuthService.Domain.Enums;

namespace Dprental.AuthService.Domain.Entities;

/// <summary>Catalog template theo dashboard type hoặc catalog độc lập đã clone cho một organization.</summary>
[Table("NavigationCatalogs", Schema = "auth")]
public class NavigationCatalog
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>Null chỉ với catalog template; catalog runtime luôn thuộc đúng một organization.</summary>
    public Guid? OrganizationId { get; set; }

    [ForeignKey(nameof(OrganizationId))]
    public Organization? Organization { get; set; }

    public OrganizationType DashboardType { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ICollection<NavigationItem> Items { get; set; } = new List<NavigationItem>();
}
