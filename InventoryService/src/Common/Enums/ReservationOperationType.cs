namespace Dprental.InventoryService.Common.Enums;

/// <summary>
/// Loại thao tác làm thay đổi trạng thái hoặc dòng thời gian của lượt đặt lịch.
/// </summary>
public enum ReservationOperationType
{
    /// <summary>
    /// Tạo lượt giữ chỗ ban đầu (None -> Held).
    /// </summary>
    CreateHold = 1,

    /// <summary>
    /// Tạo lượt đặt lịch thủ công trực tiếp (None -> Confirmed).
    /// </summary>
    CreateManual = 2,

    /// <summary>
    /// Xác nhận lượt giữ chỗ còn hạn (Held -> Confirmed).
    /// </summary>
    ConfirmHold = 3,

    /// <summary>
    /// Nhả lượt giữ chỗ chủ động (Held -> Released).
    /// </summary>
    ReleaseHold = 4,

    /// <summary>
    /// Hết hạn thời gian giữ chỗ tự động (Held -> Released).
    /// </summary>
    ExpireHold = 5,

    /// <summary>
    /// Hủy lượt đặt lịch trước khi bàn giao thiết bị (Confirmed -> Cancelled).
    /// </summary>
    Cancel = 6,

    /// <summary>
    /// Bàn giao thiết bị cho khách thuê (Confirmed -> InUse).
    /// </summary>
    Deliver = 7,

    /// <summary>
    /// Thu hồi thiết bị từ khách thuê và hoàn tất hợp đồng (InUse -> Completed).
    /// </summary>
    Return = 8,

    /// <summary>
    /// Gia hạn thời gian giữ chỗ khi hold còn hiệu lực (Held -> Held).
    /// </summary>
    ExtendHold = 9
}
