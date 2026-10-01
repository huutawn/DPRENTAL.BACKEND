using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Entities;

/// <summary>Nhóm phân loại equipment. MVP dùng danh sách phẳng, chưa có phân cấp.</summary>
[Table("Categories", Schema = "catalog")]
[Index(nameof(Slug), IsUnique = true)]
public class Category
{
    /// <summary>Id do ứng dụng tự gán, để EF luôn INSERT entity mới thay vì đoán theo giá trị khóa.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Định danh dạng URL-friendly, duy nhất trong toàn bộ catalog.</summary>
    [Required]
    [MaxLength(150)]
    public string Slug { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>Category ngừng hoạt động không nhận equipment mới và không cho publish.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<Equipment> Equipments { get; set; } = new List<Equipment>();
}
