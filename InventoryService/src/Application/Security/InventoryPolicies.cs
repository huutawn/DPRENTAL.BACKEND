namespace Dprental.InventoryService.Application.Security;

/// <summary>
/// Tên các authorization policies chuẩn của Inventory Service theo DPR-147.
/// </summary>
public static class InventoryPolicies
{
    /// <summary>
    /// Quyền quản lý tài sản: Human p=inventory.asset.manage + own asset, hoặc Admin r=adm. Service bị từ chối.
    /// </summary>
    public const string AssetManage = "InventoryAssetManage";

    /// <summary>
    /// Quyền đọc chi tiết lịch tài sản: Human p=inventory.calendar.read + own asset, hoặc Admin r=adm. Service bị từ chối.
    /// </summary>
    public const string CalendarRead = "InventoryCalendarRead";

    /// <summary>
    /// Quyền đọc lịch khả dụng đã làm sạch (Sanitized Availability): Bất kỳ Human đã xác thực, Admin r=adm, hoặc Service s=inventory.calendar.read.
    /// </summary>
    public const string SanitizedAvailability = "InventorySanitizedAvailability";

    /// <summary>
    /// Quyền gọi các lệnh phân bổ/hold (Hold/Allocation commands): Service s=inventory.allocation.write. Human bị từ chối hoàn toàn (kể cả Admin).
    /// </summary>
    public const string AllocationWrite = "InventoryAllocationWrite";

    /// <summary>
    /// Quyền tạo phân bổ thủ công (Manual allocation): Human p=inventory.asset.manage + own asset, hoặc Admin r=adm. Service bị từ chối.
    /// </summary>
    public const string ManualAllocation = "InventoryManualAllocation";
}
