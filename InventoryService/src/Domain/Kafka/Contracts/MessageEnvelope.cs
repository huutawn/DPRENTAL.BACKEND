using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Domain.Kafka.Contracts;

/// <summary>
/// Các phương thức tiện ích và factory chuẩn hóa cho MessageEnvelope.
/// </summary>
public static class MessageEnvelope
{
    public const string DefaultProducer = "InventoryService";

    /// <summary>
    /// Tạo partition key chuẩn hóa sử dụng AggregateId cho Kafka.
    /// </summary>
    public static string CreateKafkaKey(string aggregateId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateId);
        return aggregateId;
    }


    /// <summary>
    /// Factory tạo message gốc (root message): sinh MessageId mới, gán CorrelationId mới/chỉ định, CausationId là null.
    /// </summary>
    public static MessageEnvelope<T> CreateRoot<T>(
        string messageType,
        string aggregateId,
        T payload,
        string? correlationId = null,
        int version = 1,
        string producer = DefaultProducer,
        DateTimeOffset? occurredAt = null,
        Guid? organizationId = null)
    {
        var now = (occurredAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var messageId = Guid.NewGuid();
        var corrId = string.IsNullOrWhiteSpace(correlationId) ? messageId.ToString("D") : correlationId;

        var envelope = new MessageEnvelope<T>
        {
            MessageId = messageId,
            MessageType = messageType,
            Version = version,
            CorrelationId = corrId,
            CausationId = null,
            AggregateId = aggregateId,
            OrganizationId = organizationId,
            OccurredAt = now,
            Producer = producer,
            Payload = payload
        };

        envelope.Validate();
        return envelope;
    }


    /// <summary>
    /// Factory tạo message tiếp nối (continuation message): sinh MessageId mới, giữ CorrelationId, CausationId trỏ tới MessageId đầu vào.
    /// </summary>
    public static MessageEnvelope<T> CreateContinuation<T, TIn>(
        MessageEnvelope<TIn> incoming,
        string messageType,
        string aggregateId,
        T payload,
        int version = 1,
        string producer = DefaultProducer,
        DateTimeOffset? occurredAt = null,
        Guid? organizationId = null)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        var now = (occurredAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var messageId = Guid.NewGuid();

        var envelope = new MessageEnvelope<T>
        {
            MessageId = messageId,
            MessageType = messageType,
            Version = version,
            CorrelationId = incoming.CorrelationId,
            CausationId = incoming.MessageId,
            AggregateId = aggregateId,
            OrganizationId = organizationId ?? incoming.OrganizationId,
            OccurredAt = now,
            Producer = producer,
            Payload = payload
        };

        envelope.Validate();
        return envelope;
    }


    /// <summary>
    /// Factory tạo message tiếp nối từ CorrelationId và CausationId đã biết.
    /// </summary>
    public static MessageEnvelope<T> CreateContinuation<T>(
        string correlationId,
        Guid causationId,
        string messageType,
        string aggregateId,
        T payload,
        int version = 1,
        string producer = DefaultProducer,
        DateTimeOffset? occurredAt = null,
        Guid? organizationId = null)
    {
        var now = (occurredAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var messageId = Guid.NewGuid();

        var envelope = new MessageEnvelope<T>
        {
            MessageId = messageId,
            MessageType = messageType,
            Version = version,
            CorrelationId = correlationId,
            CausationId = causationId,
            AggregateId = aggregateId,
            OrganizationId = organizationId,
            OccurredAt = now,
            Producer = producer,
            Payload = payload
        };

        envelope.Validate();
        return envelope;
    }

}

/// <summary>
/// Chuẩn hóa MessageEnvelope của DPRENTAL:
/// - MessageId: Guid
/// - MessageType: string
/// - Version: int (positive integer)
/// - CorrelationId: string
/// - CausationId: Guid?
/// - AggregateId: string
/// - OrganizationId: Guid
/// - OccurredAt: DateTimeOffset (UTC)
/// - Producer: string
/// - Payload: T
/// </summary>
public sealed record MessageEnvelope<T>
{
    [JsonPropertyName("messageId")]
    public required Guid MessageId { get; init; }

    [JsonPropertyName("messageType")]
    public required string MessageType { get; init; }

    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("causationId")]
    public Guid? CausationId { get; init; }

    [JsonPropertyName("aggregateId")]
    public required string AggregateId { get; init; }

    [JsonPropertyName("organizationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? OrganizationId { get; init; }

    [JsonPropertyName("occurredAt")]
    public required DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("producer")]
    public required string Producer { get; init; }

    [JsonPropertyName("payload")]
    public required T Payload { get; init; }

    /// <summary>
    /// Lấy partition key theo AggregateId cho Kafka.
    /// </summary>
    [JsonIgnore]
    public string KafkaKey => MessageEnvelope.CreateKafkaKey(AggregateId);

    /// <summary>
    /// Kiểm tra tính hợp lệ của envelope theo hợp đồng chuẩn hóa.
    /// </summary>
    public void Validate()
    {
        if (MessageId == Guid.Empty)
        {
            throw new ArgumentException("MessageId không được rỗng.", nameof(MessageId));
        }

        if (string.IsNullOrWhiteSpace(MessageType))
        {
            throw new ArgumentException("MessageType không được rỗng.", nameof(MessageType));
        }

        if (Version <= 0)
        {
            throw new ArgumentException("Version phải là số nguyên dương lớn hơn 0.", nameof(Version));
        }

        if (string.IsNullOrWhiteSpace(CorrelationId))
        {
            throw new ArgumentException("CorrelationId không được rỗng.", nameof(CorrelationId));
        }

        if (string.IsNullOrWhiteSpace(AggregateId))
        {
            throw new ArgumentException("AggregateId không được rỗng.", nameof(AggregateId));
        }


        if (OccurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("OccurredAt phải là thời gian chuẩn UTC (offset bằng 0).", nameof(OccurredAt));
        }

        if (string.IsNullOrWhiteSpace(Producer))
        {
            throw new ArgumentException("Producer không được rỗng.", nameof(Producer));
        }

        if (Payload is null)
        {
            throw new ArgumentNullException(nameof(Payload), "Payload không được null.");
        }
    }
}
