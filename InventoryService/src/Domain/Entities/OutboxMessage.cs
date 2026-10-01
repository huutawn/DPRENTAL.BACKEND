using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể lưu trữ các thông điệp sự kiện cần phát ra ngoài (Transactional Outbox Pattern).
/// Lưu trữ trực tiếp wire JSON, topic, Kafka key và headers để publish trực tiếp không tạo lại envelope.
/// </summary>
[Table("outbox_messages", Schema = "inventory")]
public sealed class OutboxMessage
{
    /// <summary>
    /// Định danh duy nhất toàn cục của thông điệp outbox (trùng với MessageId của envelope).
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    /// <summary>
    /// Tên topic Kafka mục tiêu của thông điệp.
    /// </summary>
    [Column("topic")]
    [MaxLength(250)]
    public string Topic { get; private set; } = string.Empty;

    /// <summary>
    /// Khóa phân vùng Kafka (kết hợp OrganizationId và AggregateId).
    /// </summary>
    [Column("kafka_key")]
    [MaxLength(250)]
    public string KafkaKey { get; private set; } = string.Empty;

    /// <summary>
    /// Wire JSON UTF-8 của MessageEnvelope.
    /// </summary>
    [Column("payload", TypeName = "jsonb")]
    public string Payload { get; private set; } = string.Empty;

    /// <summary>
    /// Wire JSON của Kafka headers đi kèm nếu có.
    /// </summary>
    [Column("headers", TypeName = "jsonb")]
    public string? Headers { get; private set; }

    /// <summary>
    /// Thời điểm khởi tạo bản ghi outbox (UTC).
    /// </summary>
    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Thời điểm thông điệp được xuất bản (publish) thành công lên Message Broker và nhận broker ACK (UTC).
    /// </summary>
    [Column("published_at")]
    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>
    /// Thời điểm sớm nhất được phép thử xuất bản tiếp theo (UTC) phục vụ exponential backoff.
    /// </summary>
    [Column("next_attempt_at")]
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>
    /// Số lần đã thử xuất bản thất bại (failure count, tối đa 10 lần trước khi chuyển sang trạng thái cần phục hồi thủ công).
    /// </summary>
    [Column("retry_count")]
    public int RetryCount { get; private set; }

    /// <summary>
    /// Thông báo lỗi gần nhất khi xuất bản thất bại.
    /// </summary>
    [Column("last_error")]
    [MaxLength(2000)]
    public string? LastError { get; private set; }

    /// <summary>
    /// Token định danh của worker đang nắm giữ lease để tránh worker khác hoặc worker mất lease ghi đè.
    /// </summary>
    [Column("lock_lease_token")]
    public Guid? LockLeaseToken { get; private set; }

    /// <summary>
    /// Thời hạn khóa độc quyền xử lý (lease lock) dành cho worker đang xuất bản (UTC).
    /// </summary>
    [Column("lock_lease_expires_at")]
    public DateTimeOffset? LockLeaseExpiresAt { get; private set; }

    /// <summary>
    /// Khởi tạo private phục vụ EF Core.
    /// </summary>
    private OutboxMessage()
    {
    }

    /// <summary>
    /// Tạo mới một bản ghi Outbox lưu trữ wire JSON sẵn sàng xuất bản.
    /// </summary>
    public static OutboxMessage Create(
        Guid id,
        string topic,
        string kafkaKey,
        string payload,
        DateTimeOffset now,
        string? headers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(kafkaKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        return new OutboxMessage
        {
            Id = id,
            Topic = topic,
            KafkaKey = kafkaKey,
            Payload = payload,
            Headers = headers,
            CreatedAt = now,
            NextAttemptAt = now,
            RetryCount = 0
        };
    }

    /// <summary>
    /// Gán lease độc quyền cho worker claim thông điệp.
    /// </summary>
    public void AssignLease(Guid leaseToken, DateTimeOffset leaseExpiresAt)
    {
        LockLeaseToken = leaseToken;
        LockLeaseExpiresAt = leaseExpiresAt;
    }

    /// <summary>
    /// Đánh dấu thông điệp đã được broker xác nhận xuất bản thành công.
    /// </summary>
    public void MarkAsPublished(DateTimeOffset now)
    {
        PublishedAt = now;
        LockLeaseToken = null;
        LockLeaseExpiresAt = null;
        LastError = null;
    }

    /// <summary>
    /// Ghi nhận xuất bản thất bại kèm lịch thử lại theo exponential backoff.
    /// </summary>
    public void RecordFailure(string error, DateTimeOffset nextAttemptAt, DateTimeOffset? leaseExpiry = null)
    {
        RetryCount++;
        LastError = error;
        NextAttemptAt = nextAttemptAt;
        LockLeaseToken = null;
        LockLeaseExpiresAt = leaseExpiry;
    }

    /// <summary>
    /// Đặt lại trạng thái để phục hồi thủ công (requeue) cho thông điệp bị cạn budget retry.
    /// </summary>
    public void Requeue(DateTimeOffset now)
    {
        RetryCount = 0;
        LastError = null;
        NextAttemptAt = now;
        LockLeaseToken = null;
        LockLeaseExpiresAt = null;
    }
}
