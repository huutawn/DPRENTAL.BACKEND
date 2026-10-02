namespace Dprental.CatalogService.Common.Enums;

/// <summary>Vòng đời của một equipment type trong Canonical Catalog.</summary>
public enum EquipmentTypeStatus
{
    /// <summary>Mới tạo, chưa công khai và chưa dùng được để tạo asset.</summary>
    Draft = 0,

    /// <summary>Đã công khai; Supplier có thể tạo asset theo type này.</summary>
    Published = 1,

    /// <summary>Ngừng dùng: không tạo asset mới và không hiển thị trong catalog public. Có thể publish lại.</summary>
    Disabled = 2
}
