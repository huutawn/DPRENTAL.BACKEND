namespace Dprental.InventoryService.Common.Enums;

/// <summary>
/// Trạng thái vòng đời của một lượt phân bổ tài sản (AssetAllocation).
/// </summary>
public enum AllocationStatus
{
    /// <summary>
    /// Giữ chỗ tạm thời, có thời hạn hết hạn (HoldExpiresAt).
    /// </summary>
    Held = 1,

    /// <summary>
    /// Đã phân bổ chính thức cho lịch sử dụng/đơn thuê.
    /// </summary>
    Allocated = 2,

    /// <summary>
    /// Đã bị hủy trước khi bắt đầu hoặc theo yêu cầu scheduling.
    /// </summary>
    Cancelled = 5,

    /// <summary>
    /// Đã được giải phóng lịch (hết hạn hold, nhả hold, hoặc hoàn tất kỳ phân bổ).
    /// </summary>
    Released = 6
}
