using System.Text.Json.Serialization;

namespace Dprental.RentalService.Application.Kafka.Contracts;

/// <summary>
/// Vỏ bản tin Kafka phiên bản 1 dùng chung khi tích hợp Rental, Payment và Inventory.
/// Payload chứa dữ liệu theo MessageType; metadata giúp định danh, định tuyến và lần ngược nguyên nhân.
/// </summary>
/// <remarks>
/// MessageId nhận diện bản tin để chống xử lý trùng; AggregateId nhận diện order.
/// CorrelationId liên kết các bước cùng luồng; CausationId trỏ message dẫn tới message hiện tại.
/// Version là phiên bản envelope, khác orderVersion của dữ liệu nghiệp vụ trong payload.
/// </remarks>
public sealed record KafkaMessageEnvelope<TPayload>(
    Guid MessageId,
    string MessageType,
    int Version,
    string Producer,
    string AggregateId,
    [property: JsonConverter(typeof(UtcIsoDateTimeOffsetConverter))] DateTimeOffset OccurredAt,
    string? CorrelationId,
    Guid? CausationId,
    TPayload Payload,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? OrganizationId = null);
