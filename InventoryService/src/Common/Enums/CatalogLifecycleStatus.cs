namespace Dprental.InventoryService.Common.Enums;

/// <summary>
/// Trạng thái vòng đời của thiết bị được đồng bộ từ Catalog Service.
/// </summary>
public enum CatalogLifecycleStatus
{
    /// <summary>
    /// Thiết bị đang hoạt động bình thường và sẵn sàng cho thuê.
    /// </summary>
    Active = 1,

    /// <summary>
    /// Thiết bị tạm ngừng hoạt động trong catalog (ẩn hoặc bảo trì catalog).
    /// </summary>
    Inactive = 2,

    /// <summary>
    /// Thiết bị đã lưu trữ hoặc ngưng sử dụng vĩnh viễn trong catalog.
    /// </summary>
    Archived = 3
}
