namespace Dprental.InventoryService.Common.Enums;

/// <summary>
/// Trạng thái của EquipmentListing.
/// </summary>
public enum EquipmentListingStatus
{
    /// <summary>
    /// Bản nháp, chưa công khai và không nhận hold mới.
    /// </summary>
    Draft = 1,

    /// <summary>
    /// Đang hoạt động, được hiển thị công khai và nhận hold mới.
    /// </summary>
    Active = 2,

    /// <summary>
    /// Tạm dừng hoạt động, không hiển thị công khai và không nhận hold mới.
    /// </summary>
    Inactive = 3
}
