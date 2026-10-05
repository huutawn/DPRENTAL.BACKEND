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
    /// Quyền truy cập lịch tài sản (DPR-149): Human own asset (OwnerUserId == sub, không cần p). Admin được phép nếu là owner, không bypass. Service bị từ chối.
    /// </summary>
    public const string CalendarRead = "InventoryCalendarRead";

    /// <summary>
    /// Quyền đọc lịch khả dụng (Availability Read): Bất kỳ Human đã xác thực, hoặc Service u=svc có scope s=inventory.availability.read.
    /// </summary>
    public const string AvailabilityRead = "InventoryAvailabilityRead";

    /// <summary>
    /// Quyền gọi các lệnh phân bổ/hold (Hold/Allocation commands): Service s=inventory.allocation.write. Human bị từ chối hoàn toàn (kể cả Admin).
    /// </summary>
    public const string AllocationWrite = "InventoryAllocationWrite";

    /// <summary>
    /// Quyền tạo phân bổ thủ công (Manual allocation): Human p=inventory.asset.manage + own asset, hoặc Admin r=adm. Service bị từ chối.
    /// </summary>
    public const string ManualAllocation = "InventoryManualAllocation";
}
