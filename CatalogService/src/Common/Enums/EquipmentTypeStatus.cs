namespace Dprental.CatalogService.Common.Enums;

/// <summary>
/// Vòng đời của một equipment type trong Catalog.
/// </summary>
public enum EquipmentTypeStatus
{
    /// <summary>Mới tạo, chưa hiển thị trên public catalog.</summary>
    Draft = 0,

    /// <summary>Đang hiển thị trên public catalog.</summary>
    Published = 1,

    /// <summary>Đã bị vô hiệu hóa khỏi public catalog, có thể publish lại.</summary>
    Disabled = 2
}
