namespace Dprental.CatalogService.Domain.Entities;

/// <summary>
/// Tham chiếu từ equipment tới một media asset của Media Service.
/// Catalog không lưu đường dẫn, bucket, MIME type hay URL của tệp.
/// </summary>
public class EquipmentMedia
{
    public Guid Id { get; set; }

    public Guid EquipmentId { get; set; }

    /// <summary>
    /// Định danh media asset bên Media Service. Chỉ là tham chiếu logic, không có FK.
    /// </summary>
    public Guid MediaId { get; set; }

    /// <summary>
    /// Thứ tự hiển thị trong gallery, số nhỏ hiển thị trước.
    /// </summary>
    public int DisplayOrder { get; set; }

    /// <summary>
    /// Ảnh đại diện của listing. Mỗi equipment có tối đa một thumbnail.
    /// </summary>
    public bool IsThumbnail { get; set; }

    public Equipment? Equipment { get; set; }
}
