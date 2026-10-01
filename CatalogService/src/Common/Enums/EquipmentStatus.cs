namespace Dprental.CatalogService.Common.Enums;

/// <summary>
/// Vòng đời của một equipment listing trong MVP.
/// </summary>
public enum EquipmentStatus
{
    /// <summary>Mới tạo, chưa hiển thị trên public catalog.</summary>
    Draft = 0,

    /// <summary>Đang hiển thị trên public catalog.</summary>
    Published = 1,

    /// <summary>Đã bị ẩn khỏi public catalog, có thể publish lại.</summary>
    Hidden = 2
}
