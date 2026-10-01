namespace Dprental.InventoryService.Common.Enums;

/// <summary>
/// Trạng thái xử lý của thông điệp nhận vào trong Inbox pattern.
/// </summary>
public enum InboxMessageStatus
{
    /// <summary>
    /// Đã tiếp nhận thông điệp, chờ đến lượt xử lý.
    /// </summary>
    Received = 1,

    /// <summary>
    /// Đang trong tiến trình xử lý thông điệp.
    /// </summary>
    Processing = 2,

    /// <summary>
    /// Đã xử lý thông điệp thành công.
    /// </summary>
    Processed = 3,

    /// <summary>
    /// Xử lý thất bại và chờ xử lý lại hoặc chuyển vào dead-letter.
    /// </summary>
    Failed = 4
}
