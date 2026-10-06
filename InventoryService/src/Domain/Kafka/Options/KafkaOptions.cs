namespace Dprental.InventoryService.Domain.Kafka.Options;

/// <summary>
/// Cấu hình kết nối và hành vi của Kafka Producer, Consumer và AdminClient.
/// </summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    /// <summary>
    /// Địa chỉ bootstrap servers của Kafka (ví dụ: localhost:29092).
    /// </summary>
    public string BootstrapServers { get; set; } = "localhost:29092";

    /// <summary>
    /// Định danh client gửi lên broker.
    /// </summary>
    public string ClientId { get; set; } = "inventory-service";

    /// <summary>
    /// Thời hạn hữu hạn để producer gửi và nhận xác nhận từ broker (ms). Mặc định 15,000ms.
    /// </summary>
    public int DeliveryTimeoutMs { get; set; } = 15000;

    /// <summary>
    /// Mức xác nhận ghi của Producer (all / -1).
    /// </summary>
    public string Acks { get; set; } = "all";

    /// <summary>
    /// Bật tính năng Idempotent Producer để ngăn trùng lặp thông điệp trên broker.
    /// </summary>
    public bool EnableIdempotence { get; set; } = true;

    /// <summary>
    /// Tự động provision các topic baseline và DLQ trong môi trường Development qua AdminClient.
    /// </summary>
    public bool AutoProvisionTopics { get; set; } = true;

    /// <summary>
    /// Số partitions mặc định khi provision topic baseline (mặc định 3 partitions).
    /// </summary>
    public int DefaultPartitions { get; set; } = 3;

    /// <summary>
    /// Replication factor mặc định (mặc định 1).
    /// </summary>
    public short DefaultReplicationFactor { get; set; } = 1;

    /// <summary>
    /// Session timeout của Consumer (ms).
    /// </summary>
    public int SessionTimeoutMs { get; set; } = 45000;

    /// <summary>
    /// Khoảng thời gian tối đa giữa các lần poll trước khi consumer bị coi là chết (ms).
    /// </summary>
    public int MaxPollIntervalMs { get; set; } = 300000;

    /// <summary>
    /// Vị trí đọc offset ban đầu khi không tìm thấy committed offset (earliest).
    /// </summary>
    public string AutoOffsetReset { get; set; } = "earliest";
}
