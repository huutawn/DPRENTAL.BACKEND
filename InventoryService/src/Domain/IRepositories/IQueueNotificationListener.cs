namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Định danh các loại hàng đợi nền của InventoryService.
/// </summary>
public enum QueueWorkKind
{
    Outbox,
    CacheInvalidation,
    SearchProjection
}

/// <summary>
/// Hợp đồng nhận tín hiệu thông báo công việc nền từ PostgreSQL pg_notify.
/// Quản lý tín hiệu trong bộ nhớ có dung lượng giới hạn, gộp tín hiệu trùng (coalescing),
/// và quản lý trạng thái kết nối của listener.
/// </summary>
public interface IQueueNotificationListener
{
    /// <summary>
    /// Cho biết listener hiện có đang kết nối và lắng nghe PostgreSQL NOTIFY thành công hay không.
    /// Khi false, worker sẽ fallback sang polling 1 giây.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Chờ tín hiệu có việc mới của hàng đợi được chỉ định hoặc timeout.
    /// </summary>
    ValueTask<bool> WaitForWorkAsync(QueueWorkKind queueKind, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Đánh thức thủ công một hàng đợi cụ thể trong bộ nhớ (ví dụ test hoặc sau khi reconnect).
    /// </summary>
    void TriggerWakeUp(QueueWorkKind queueKind);

    /// <summary>
    /// Đánh thức tất cả các hàng đợi.
    /// </summary>
    void TriggerAll();
}
