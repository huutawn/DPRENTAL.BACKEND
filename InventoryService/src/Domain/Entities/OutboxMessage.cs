using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể lưu trữ các thông điệp sự kiện cần phát ra ngoài (Transactional Outbox Pattern).
/// Đảm bảo tính nhất quán cuối cùng (Eventual Consistency) giữa cơ sở dữ liệu nội bộ và Message Broker.
/// </summary>
[Table("outbox_messages", Schema = "inventory")]
public sealed class OutboxMessage
{
    /// <summary>
    /// Định danh duy nhất toàn cục của thông điệp outbox.
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    /// <summary>
    /// Tên loại sự kiện miền nghiệp vụ (ví dụ: InventoryEquipmentHoldCreated, ReservationCompleted).
    /// </summary>
    [Column("event_type")]
    [MaxLength(200)]
    public string EventType { get; private set; } = string.Empty;

    /// <summary>
    /// Phiên bản schema của thông điệp sự kiện.
    /// </summary>
    [Column("event_version")]
    public int EventVersion { get; private set; } = 1;

    /// <summary>
    /// Loại aggregate phát sinh sự kiện (Equipment, Reservation, Block...).
    /// </summary>
    [Column("aggregate_type")]
    [MaxLength(100)]
    public string AggregateType { get; private set; } = string.Empty;

    /// <summary>
    /// Định danh của aggregate phát sinh sự kiện dưới dạng chuỗi.
    /// </summary>
    [Column("aggregate_id")]
    [MaxLength(100)]
    public string AggregateId { get; private set; } = string.Empty;

    /// <summary>
    /// Mã tương quan chuỗi xử lý (Correlation ID) phục vụ truy vết phân tán.
    /// </summary>
    [Column("correlation_id")]
    [MaxLength(100)]
    public string? CorrelationId { get; private set; }

    /// <summary>
    /// Nội dung chi tiết của sự kiện dưới định dạng JSONB của PostgreSQL.
    /// </summary>
    [Column("payload", TypeName = "jsonb")]
    public string Payload { get; private set; } = string.Empty;

    /// <summary>
    /// Thời điểm khởi tạo bản ghi outbox (UTC).
    /// </summary>
    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Thời điểm thông điệp được xuất bản (publish) thành công lên Message Broker (UTC).
    /// </summary>
    [Column("processed_at")]
    public DateTimeOffset? ProcessedAt { get; private set; }

    /// <summary>
    /// Số lần đã thử xuất bản lại khi gặp lỗi.
    /// </summary>
    [Column("retry_count")]
    public int RetryCount { get; private set; }

    /// <summary>
    /// Thời hạn khóa độc quyền xử lý (lease lock) dành cho worker đang xử lý thông điệp này (UTC).
    /// </summary>
    [Column("lock_lease_expires_at")]
    public DateTimeOffset? LockLeaseExpiresAt { get; private set; }

    /// <summary>
    /// Thông báo lỗi gần nhất nếu quá trình xuất bản thất bại.
    /// </summary>
    [Column("last_error")]
    [MaxLength(2000)]
    public string? LastError { get; private set; }

    /// <summary>
    /// Khởi tạo private phục vụ EF Core.
    /// </summary>
    private OutboxMessage()
    {
    }

    /// <summary>
    /// Tạo mới một thông điệp Outbox sẵn sàng để xuất bản.
    /// </summary>
    /// <param name="id">ID thông điệp.</param>
    /// <param name="eventType">Loại sự kiện.</param>
    /// <param name="aggregateType">Loại aggregate.</param>
    /// <param name="aggregateId">ID aggregate.</param>
    /// <param name="payload">Nội dung JSONB.</param>
    /// <param name="now">Thời điểm tạo (UTC).</param>
    /// <param name="correlationId">Correlation ID nếu có.</param>
    /// <param name="eventVersion">Phiên bản sự kiện (mặc định 1).</param>
    public static OutboxMessage Create(
        Guid id,
        string eventType,
        string aggregateType,
        string aggregateId,
        string payload,
        DateTimeOffset now,
        string? correlationId = null,
        int eventVersion = 1)
    {
        return new OutboxMessage
        {
            Id = id,
            EventType = eventType,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            Payload = payload,
            CreatedAt = now,
            CorrelationId = correlationId,
            EventVersion = eventVersion,
            RetryCount = 0
        };
    }

    /// <summary>
    /// Đánh dấu thông điệp đã được xuất bản thành công lên Message Broker.
    /// </summary>
    /// <param name="now">Thời điểm xuất bản (UTC).</param>
    public void MarkAsProcessed(DateTimeOffset now)
    {
        ProcessedAt = now;
        LockLeaseExpiresAt = null;
        LastError = null;
    }

    /// <summary>
    /// Ghi nhận xuất bản thất bại và gia tăng bộ đếm số lần retry.
    /// </summary>
    /// <param name="error">Nội dung lỗi gặp phải.</param>
    /// <param name="leaseExpiry">Thời hạn nhả khóa lease.</param>
    public void RecordFailure(string error, DateTimeOffset? leaseExpiry = null)
    {
        RetryCount++;
        LastError = error;
        LockLeaseExpiresAt = leaseExpiry;
    }
}
