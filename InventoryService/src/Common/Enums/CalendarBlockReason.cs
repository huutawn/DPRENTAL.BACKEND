namespace Dprental.InventoryService.Common.Enums;

/// <summary>
/// Lý do chặn lịch sử dụng thiết bị (Calendar Block).
/// </summary>
public enum CalendarBlockReason
{
    /// <summary>
    /// Chặn lịch để thực hiện bảo trì, sửa chữa hoặc bảo dưỡng định kỳ.
    /// </summary>
    Maintenance = 1,

    /// <summary>
    /// Chủ sở hữu hoặc tổ chức giữ thiết bị để sử dụng nội bộ riêng.
    /// </summary>
    OwnerReserved = 2,

    /// <summary>
    /// Chặn lịch phục vụ công tác kiểm định, đánh giá chất lượng hoặc bàn giao nội bộ.
    /// </summary>
    Inspection = 3,

    /// <summary>
    /// Lý do vận hành khác.
    /// </summary>
    Other = 4
}
