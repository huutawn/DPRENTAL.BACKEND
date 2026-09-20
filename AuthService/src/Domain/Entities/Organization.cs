using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

/// <summary>Loại organization theo vai trò trong nền tảng.</summary>
public enum OrganizationType
{
    Platform,
    Supplier,
    Renter
}

/// <summary>Một tổ chức (Platform/Supplier/Renter) mà user có thể là thành viên.</summary>
[Table("Organizations", Schema = "auth")]
[Index(nameof(Slug), IsUnique = true)]
public class Organization
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Định danh dạng URL-friendly, ổn định, dùng để tham chiếu thay vì Id trong URL public.</summary>
    [Required]
    [MaxLength(100)]
    public string Slug { get; set; } = string.Empty;

    public OrganizationType Type { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ICollection<OrganizationMembership> Memberships { get; set; } = new List<OrganizationMembership>();
}
