using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Trạng thái kết quả thực thi trong execution boundary của Inbox.
/// </summary>
public enum InboxProcessingStatus
{
    /// <summary>
    /// Xử lý thành công và đã commit cùng thay đổi nghiệp vụ.
    /// </summary>
    Success,

    /// <summary>
    /// Thông điệp đã được xử lý thành công trước đó (Idempotent replay).
    /// </summary>
    AlreadyProcessed,

    /// <summary>
    /// Lỗi tạm thời, đã rollback nghiệp vụ và lên lịch retry theo exponential backoff.
    /// </summary>
    TransientFailure,

    /// <summary>
    /// Cạn budget retry (5 lần) hoặc format không hợp lệ, đã commit DeadLettered và Outbox DLQ atomically.
    /// </summary>
    DeadLettered
}

/// <summary>
/// Kết quả trả về từ execution boundary của Inbox.
/// </summary>
public sealed record InboxExecutionResult<T>(
    InboxProcessingStatus Status,
    T? Value = default,
    Error? Error = null,
    int RetryCount = 0);

/// <summary>
/// Hợp đồng lưu trữ và thực thi transaction boundary của Transactional Inbox pattern.
/// </summary>
public interface IInboxStore
{
    /// <summary>
    /// Kiểm tra xem thông điệp có messageId đã từng được ghi nhận bởi handler này hay chưa.
    /// </summary>
    Task<bool> HasBeenReceivedAsync(
        string handler,
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Kiểm tra xem thông điệp đã hoàn tất xử lý thành công (Status = Processed) hay chưa.
    /// </summary>
    Task<bool> HasBeenProcessedAsync(
        string handler,
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy thông tin bản ghi inbox theo handler và messageId.
    /// </summary>
    Task<InboxMessage?> GetAsync(
        string handler,
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Thêm bản ghi thông điệp mới vào Inbox.
    /// </summary>
    void Add(InboxMessage message);

    /// <summary>
    /// Cập nhật trạng thái xử lý của bản ghi Inbox.
    /// </summary>
    void Update(InboxMessage message);

    /// <summary>
    /// Thực thi handler trong execution boundary:
    /// 1. Khóa receipt bản ghi Inbox
    /// 2. Bỏ qua an toàn nếu đã xử lý thành công trước đó (idempotency)
    /// 3. Khởi tạo transaction trên DbContext (nếu chưa có)
    /// 4. Chạy handler operation
    /// 5. Commit atomically: nghiệp vụ + staged Outbox/Saga + ProcessedAt
    /// 6. Nếu lỗi: rollback nghiệp vụ, cập nhật failure và lịch retry; nếu cạn budget thì commit DLQ
    /// </summary>
    Task<InboxExecutionResult<T>> ExecuteInTransactionAsync<T>(
        string handler,
        string messageId,
        DateTimeOffset now,
        Func<CancellationToken, Task<Result<T>>> operation,
        Func<int, string, OutboxMessage>? dlqMessageFactory = null,
        CancellationToken cancellationToken = default);
}
