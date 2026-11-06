using Confluent.Kafka;

namespace Dprental.RentalService.Application.Kafka.Configuration;

/// <remarks>
/// Cấu hình dùng chung cho transport, consumer, publisher và Inbox worker; bind từ section Kafka.
/// Enabled mặc định false. KafkaOptionsValidator kiểm tra các điều kiện bắt buộc lúc startup khi bật Kafka.
/// </remarks>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    /// <summary>
    /// Kafka mặc định tắt; phải bật rõ ràng để worker và transport giao tiếp broker.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Danh sách địa chỉ bootstrap của broker mà tiến trình Rental truy cập được.</summary>
    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>Tên định danh client gửi bản tin, dùng để theo dõi tại broker.</summary>
    public string ClientId { get; set; } = "rental-service";

    /// <summary>Consumer group dùng chung cho sự kiện Payment và kết quả Inventory; group quyết định offsets đã commit.</summary>
    public string ConsumerGroupId { get; set; } = "rental-payment-events-consumer";

    // Topic sự kiện Rental, Payment và DLQ của Payment.
    /// <summary>Topic Rental phát sự kiện nghiệp vụ ra ngoài qua Outbox.</summary>
    public string RentalEventsTopic { get; set; } = "rental.events.v1";
    /// <summary>Topic Rental nhận InvoicePaid, RefundCompleted và RentalSettled.</summary>
    public string PaymentEventsTopic { get; set; } = "payment.events.v1";
    /// <summary>Topic chứa bản tin Payment lỗi được dispatcher ghi bền vững qua Outbox.</summary>
    public string PaymentDlqTopic { get; set; } = "payment.events.v1.dlq";

    // Topic lệnh và kết quả Inventory, kèm DLQ riêng.
    /// <summary>Topic phát lệnh allocation, bàn giao, trả máy và bảo vệ quá hạn tới Inventory.</summary>
    public string InventoryCommandsTopic { get; set; } = "inventory.allocation.commands.v1";
    /// <summary>Topic nhận kết quả thực thi lệnh từ Inventory, bao gồm kết quả trả từng unit.</summary>
    public string InventoryAllocationEventsTopic { get; set; } = "inventory.allocation.events.v1";
    /// <summary>Topic DLQ dành cho bản tin kết quả Inventory không xử lý được.</summary>
    public string InventoryAllocationEventsDlqTopic { get; set; } = "inventory.allocation.events.v1.dlq";

    /// <summary>Ánh xạ keyId sang public key P-256 dạng base64 SPKI để xác minh proof phát sự kiện Payment.</summary>
    public Dictionary<string, string> PaymentProofPublicKeys { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Issuer phải khớp trong proof phát sự kiện Payment.</summary>
    public string PaymentProofIssuer { get; set; } = "dprental-integrity-host";
    /// <summary>Môi trường phải khớp trong proof, tránh nhận nhầm sự kiện giữa các môi trường.</summary>
    public string PaymentProofEnvironment { get; set; } = "production";

    // Tham số bảo mật kết nối broker; không ghi log mật khẩu hoặc khóa riêng.
    public SecurityProtocol? SecurityProtocol { get; set; }
    public SaslMechanism? SaslMechanism { get; set; }
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
    public string? SslCaLocation { get; set; }
    public string? SslCertificateLocation { get; set; }
    public string? SslKeyLocation { get; set; }

    // Ngân sách thời gian, lease và chu kỳ quét.
    /// <summary>Ngân sách chờ gửi một bản tin; publisher chỉ đánh dấu thành công sau xác nhận Persisted.</summary>
    public int DeliveryTimeoutSeconds { get; set; } = 10;
    /// <summary>Thời gian publisher giữ quyền claim một bản tin; phải đủ dài so với timeout gửi.</summary>
    public int LeaseDurationSeconds { get; set; } = 30;
    /// <summary>Số bản tin tối đa publisher xử lý mỗi vòng; claim lần lượt từng bản tin.</summary>
    public int MaxBatchPerRound { get; set; } = 50;
    /// <summary>Thời gian nghỉ khi vòng quét Outbox không gửi được bản tin nào, tính bằng giây; còn việc thì publisher chạy vòng tiếp ngay.</summary>
    public int OutboxPollingIntervalSeconds { get; set; } = 1;
    /// <summary>Thời gian nghỉ khi vòng xử lý Inbox không xử lý được bản tin nào, tính bằng giây; còn việc thì worker chạy vòng tiếp ngay.</summary>
    public int InboxPollingIntervalSeconds { get; set; } = 1;
    /// <summary>Ngân sách flush producer khi publisher dừng, tính bằng giây.</summary>
    public int ShutdownTimeoutSeconds { get; set; } = 15;
    /// <summary>Thời gian tối đa một lần poll broker chờ bản tin, tính bằng mili giây.</summary>
    public int ConsumeTimeoutMilliseconds { get; set; } = 500;
}
