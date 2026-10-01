using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Entities;

/// <summary>
/// Tham chiếu từ equipment tới một media asset của Media Service.
/// Catalog không lưu đường dẫn, bucket, MIME type hay URL của tệp.
/// </summary>
[Table("EquipmentMedia", Schema = "catalog")]
// Một media chỉ được gắn một lần vào cùng một equipment.
[Index(nameof(EquipmentId), nameof(MediaId), IsUnique = true)]
public class EquipmentMedia
{
    /// <summary>Id do ứng dụng tự gán, để media mới thêm vào equipment đã load luôn được INSERT.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; }

    public Guid EquipmentId { get; set; }

    /// <summary>Xóa equipment thì xóa luôn các tham chiếu media của nó.</summary>
    [ForeignKey(nameof(EquipmentId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Equipment Equipment { get; set; } = null!;

    /// <summary>Định danh media asset bên Media Service. Chỉ là tham chiếu logic, không có FK.</summary>
    public Guid MediaId { get; set; }

    /// <summary>Thứ tự hiển thị trong gallery, số nhỏ hiển thị trước.</summary>
    public int DisplayOrder { get; set; }

    /// <summary>Ảnh đại diện của listing. Mỗi equipment có tối đa một thumbnail.</summary>
    public bool IsThumbnail { get; set; }
}
