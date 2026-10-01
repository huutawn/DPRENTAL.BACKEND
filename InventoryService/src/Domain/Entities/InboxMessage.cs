using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể lưu trữ các thông điệp nhận từ bên ngoài (Transactional Inbox Pattern).
/// Đảm bảo tính duy nhất và idempotency khi xử lý thông điệp từ Message Broker, ngăn ngừa duplicate message.
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
    /// Tên của Consumer hoặc dịch vụ tiếp nhận xử lý thông điệp.
    /// </summary>
    [Column("consumer")]
    [MaxLength(200)]
    public string Consumer { get; private set; } = string.Empty;

    /// <summary>
    /// Mã định danh thông điệp từ Message Broker (kết hợp với Consumer để tạo khóa duy nhất).
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
    /// Thời điểm xử lý thông điệp thành công hoàn tất (UTC).
    /// </summary>
    [Column("processed_at")]
    public DateTimeOffset? ProcessedAt { get; private set; }

    /// <summary>
    /// Trạng thái tiến trình xử lý thông điệp (Received, Processing, Processed, Failed).
    /// </summary>
    [Column("status")]
    public InboxMessageStatus Status { get; private set; }

    /// <summary>
    /// Số lần đã thử xử lý lại thông điệp khi gặp lỗi.
    /// </summary>
    [Column("retry_count")]
    public int RetryCount { get; private set; }

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
    /// <param name="id">ID bản ghi.</param>
    /// <param name="consumer">Tên consumer tiếp nhận.</param>
    /// <param name="messageId">ID thông điệp từ broker.</param>
    /// <param name="now">Thời điểm nhận (UTC).</param>
    public static InboxMessage Create(
        Guid id,
        string consumer,
        string messageId,
        DateTimeOffset now)
    {
        return new InboxMessage
        {
            Id = id,
            Consumer = consumer,
            MessageId = messageId,
            ReceivedAt = now,
            Status = InboxMessageStatus.Received,
            RetryCount = 0
        };
    }

    /// <summary>
    /// Đánh dấu thông điệp đã được xử lý hoàn tất thành công.
    /// </summary>
    /// <param name="now">Thời điểm hoàn tất (UTC).</param>
    public void MarkAsProcessed(DateTimeOffset now)
    {
        Status = InboxMessageStatus.Processed;
        ProcessedAt = now;
        LockLeaseExpiresAt = null;
        Error = null;
    }

    /// <summary>
    /// Ghi nhận xử lý thông điệp thất bại kèm nội dung lỗi.
    /// </summary>
    /// <param name="error">Mô tả lỗi.</param>
    /// <param name="leaseExpiry">Thời hạn khóa lease.</param>
    public void RecordFailure(string error, DateTimeOffset? leaseExpiry = null)
    {
        Status = InboxMessageStatus.Failed;
        RetryCount++;
        Error = error;
        LockLeaseExpiresAt = leaseExpiry;
    }
}
