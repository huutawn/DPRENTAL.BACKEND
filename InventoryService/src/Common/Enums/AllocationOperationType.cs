namespace Dprental.InventoryService.Common.Enums;

/// <summary>
/// Loại thao tác làm thay đổi trạng thái hoặc dòng thời gian của lượt phân bổ tài sản.
/// </summary>
public enum AllocationOperationType
{
    /// <summary>
    /// Tạo lượt giữ chỗ ban đầu (None -> Held).
    /// </summary>
    CreateHold = 1,

    /// <summary>
    /// Tạo lượt phân bổ thủ công trực tiếp (None -> Allocated).
    /// </summary>
    CreateManual = 2,

    /// <summary>
    /// Chuyển đổi lượt giữ chỗ còn hạn sang phân bổ chính thức (Held -> Allocated).
    /// </summary>
    Allocate = 3,

    /// <summary>
    /// Nhả lượt giữ chỗ chủ động (Held -> Released).
    /// </summary>
    ReleaseHold = 4,

    /// <summary>
    /// Hết hạn thời gian giữ chỗ tự động (Held -> Released).
    /// </summary>
    ExpireHold = 5,

    /// <summary>
    /// Hủy lượt phân bổ (Allocated -> Cancelled).
    /// </summary>
    Cancel = 6,

    /// <summary>
    /// Giải phóng lượt phân bổ theo yêu cầu scheduling (Allocated -> Released).
    /// </summary>
    Release = 7,

    /// <summary>
    /// Gia hạn thời gian giữ chỗ khi hold còn hiệu lực (Held -> Held).
    /// </summary>
    ExtendHold = 9
}
