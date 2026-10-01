using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể lưu trữ các thông điệp nhận từ bên ngoài (Transactional Inbox Pattern).
/// Đảm bảo tính duy nhất và idempotency khi xử lý thông điệp từ Message Broker theo cặp (Handler, MessageId).
/// </summary>
[Table("inbox_messages", Schema = "inventory")]
public sealed class InboxMessage
{
    /// <summary>
    /// Định danh duy nhất toàn cục của bản ghi inbox.
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    /// <summary>
    /// Tên của Handler tiếp nhận và xử lý thông điệp (ví dụ: EquipmentRegisteredHandler).
    /// </summary>
    [Column("handler")]
    [MaxLength(200)]
    public string Handler { get; private set; } = string.Empty;

    /// <summary>
    /// Mã định danh thông điệp từ Message Broker (kết hợp với Handler để tạo khóa duy nhất).
    /// </summary>
    [Column("message_id")]
    [MaxLength(100)]
    public string MessageId { get; private set; } = string.Empty;

    /// <summary>
    /// Thời điểm tiếp nhận thông điệp vào inbox (UTC).
    /// </summary>
    [Column("received_at")]
    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>
    /// Thời điểm xử lý thông điệp thành công hoàn tất (UTC). Chỉ khi có giá trị này thì nghiệp vụ mới được xem là hoàn tất.
    /// </summary>
    [Column("processed_at")]
    public DateTimeOffset? ProcessedAt { get; private set; }

    /// <summary>
    /// Trạng thái tiến trình xử lý thông điệp (Received, Processing, Processed, Failed, DeadLettered).
    /// </summary>
    [Column("status")]
    public InboxMessageStatus Status { get; private set; }

    /// <summary>
    /// Số lần đã thử xử lý thông điệp (bao gồm lần đầu, tối đa 5 lần).
    /// </summary>
    [Column("retry_count")]
    public int RetryCount { get; private set; }

    /// <summary>
    /// Thời điểm sớm nhất được phép thử lại xử lý thông điệp (UTC) theo exponential backoff.
    /// </summary>
    [Column("next_attempt_at")]
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>
    /// Thời hạn khóa độc quyền xử lý của worker đang giữ thông điệp (UTC).
    /// </summary>
    [Column("lock_lease_expires_at")]
    public DateTimeOffset? LockLeaseExpiresAt { get; private set; }

    /// <summary>
    /// Chi tiết lỗi khi xử lý thất bại nếu có.
    /// </summary>
    [Column("error")]
    [MaxLength(2000)]
    public string? Error { get; private set; }

    /// <summary>
    /// Khởi tạo private phục vụ EF Core.
    /// </summary>
    private InboxMessage()
    {
    }

    /// <summary>
    /// Tạo mới một bản ghi tiếp nhận thông điệp vào Inbox.
    /// </summary>
    public static InboxMessage Create(
        Guid id,
        string handler,
        string messageId,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        return new InboxMessage
        {
            Id = id,
            Handler = handler,
            MessageId = messageId,
            ReceivedAt = now,
            Status = InboxMessageStatus.Received,
            RetryCount = 0,
            NextAttemptAt = now
        };
    }

    /// <summary>
    /// Đánh dấu thông điệp đang được xử lý trong transaction boundary.
    /// </summary>
    public void MarkAsProcessing(DateTimeOffset now, DateTimeOffset leaseExpiry)
    {
        Status = InboxMessageStatus.Processing;
        LockLeaseExpiresAt = leaseExpiry;
        Error = null;
    }

    /// <summary>
    /// Đánh dấu thông điệp đã được xử lý hoàn tất thành công.
    /// </summary>
    public void MarkAsProcessed(DateTimeOffset now)
    {
        Status = InboxMessageStatus.Processed;
        ProcessedAt = now;
        LockLeaseExpiresAt = null;
        Error = null;
    }

    /// <summary>
    /// Ghi nhận xử lý thông điệp thất bại kèm lịch retry tiếp theo.
    /// </summary>
    public void RecordFailure(string error, DateTimeOffset nextAttemptAt, DateTimeOffset? leaseExpiry = null)
    {
        Status = InboxMessageStatus.Failed;
        RetryCount++;
        Error = error;
        NextAttemptAt = nextAttemptAt;
        LockLeaseExpiresAt = leaseExpiry;
    }

    /// <summary>
    /// Đánh dấu thông điệp đã chuyển vào Dead Letter Queue (DLQ) do hết retry budget hoặc format không hợp lệ.
    /// </summary>
    public void MarkAsDeadLettered(string error, DateTimeOffset now)
    {
        Status = InboxMessageStatus.DeadLettered;
        RetryCount++;
        Error = error;
        ProcessedAt = now;
        LockLeaseExpiresAt = null;
    }
}
