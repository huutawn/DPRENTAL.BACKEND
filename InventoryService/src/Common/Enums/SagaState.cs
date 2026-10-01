namespace Dprental.InventoryService.Common.Enums;

/// <summary>
/// Trạng thái của một phiên phối hợp saga phân tán.
/// </summary>
public enum SagaState
{
    /// <summary>
    /// Saga vừa được khởi tạo, chưa thực thi bước nào.
    /// </summary>
    Started = 1,

    /// <summary>
    /// Saga đang thực hiện các bước tiến hành theo kịch bản.
    /// </summary>
    Executing = 2,

    /// <summary>
    /// Toàn bộ các bước của saga đã hoàn tất thành công.
    /// </summary>
    Completed = 3,

    /// <summary>
    /// Saga gặp lỗi và đang thực thi các bước bù trừ (compensation).
    /// </summary>
    Compensating = 4,

    /// <summary>
    /// Saga thất bại hoàn toàn sau khi đã bù trừ hoặc do lỗi nghiêm trọng.
    /// </summary>
    Failed = 5
}
