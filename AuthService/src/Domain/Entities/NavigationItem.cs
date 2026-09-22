using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

/// <summary>Một menu item trong một dashboard catalog; href là route nội bộ của frontend.</summary>
[Table("NavigationItems", Schema = "auth")]
[Index(nameof(CatalogId), nameof(Key), IsUnique = true)]
public class NavigationItem
{
    [Key]
    public Guid Id { get; set; }

    public Guid CatalogId { get; set; }

    [ForeignKey(nameof(CatalogId))]
    public NavigationCatalog Catalog { get; set; } = null!;

    public Guid? ParentId { get; set; }

    [ForeignKey(nameof(ParentId))]
    public NavigationItem? Parent { get; set; }

    public ICollection<NavigationItem> Children { get; set; } = new List<NavigationItem>();

    /// <summary>Template source khi item được clone; null với template và item tạo trực tiếp trong catalog organization.</summary>
    public Guid? SourceTemplateItemId { get; set; }

    [Required]
    [MaxLength(120)]
    public string Key { get; set; } = string.Empty;

    [Required]
    [MaxLength(150)]
    public string Label { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Href { get; set; }

    [Required]
    [MaxLength(100)]
    public string Icon { get; set; } = string.Empty;

    public int Order { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<NavigationItemPermission> Permissions { get; set; } = new List<NavigationItemPermission>();
}
