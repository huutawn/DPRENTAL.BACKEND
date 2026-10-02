namespace Dprental.CatalogService.Domain.Entities;

/// <summary>
/// Tham chiếu từ equipment type tới một media asset của Media Service.
/// Catalog không lưu đường dẫn, bucket, MIME type hay URL của tệp.
/// </summary>
public class EquipmentTypeMedia
{
    /// <summary>Id do ứng dụng tự gán.</summary>
    public Guid Id { get; set; }

    public Guid EquipmentTypeId { get; set; }

    /// <summary>Xóa equipment type thì xóa luôn các tham chiếu media của nó.</summary>
    public EquipmentType EquipmentType { get; set; } = null!;

    /// <summary>Định danh media asset bên Media Service. Chỉ là tham chiếu logic, không có FK.</summary>
    public Guid MediaId { get; set; }

    /// <summary>Thứ tự hiển thị trong gallery, số nhỏ hiển thị trước.</summary>
    public int DisplayOrder { get; set; }

    /// <summary>Ảnh đại diện của equipment type. Mỗi equipment type có tối đa một thumbnail.</summary>
    public bool IsThumbnail { get; set; }
}
