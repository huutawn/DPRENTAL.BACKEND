using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.Kafka.Contracts;

namespace Dprental.InventoryService.Application.Kafka.Transport;

/// <summary>
/// Serialization và Deserialization chuẩn hóa cho MessageEnvelope theo JSON UTF-8 camelCase.
/// </summary>
public static class KafkaEnvelopeSerializer
{
    public static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Serialize MessageEnvelope thành chuỗi wire JSON UTF-8 camelCase.
    /// </summary>
    public static string Serialize<T>(MessageEnvelope<T> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        envelope.Validate();
        return JsonSerializer.Serialize(envelope, DefaultOptions);
    }

    /// <summary>
    /// Serialize MessageEnvelope thành mảng byte UTF-8.
    /// </summary>
    public static byte[] SerializeToUtf8Bytes<T>(MessageEnvelope<T> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        envelope.Validate();
        return JsonSerializer.SerializeToUtf8Bytes(envelope, DefaultOptions);
    }

    /// <summary>
    /// Deserialize chuỗi JSON thành MessageEnvelope typed.
    /// </summary>
    public static MessageEnvelope<T> Deserialize<T>(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var envelope = JsonSerializer.Deserialize<MessageEnvelope<T>>(json, DefaultOptions)
            ?? throw new JsonException("Không thể deserialize MessageEnvelope từ wire JSON.");
        envelope.Validate();
        return envelope;
    }

    /// <summary>
    /// Deserialize chuỗi JSON thành MessageEnvelope typed và thực thi Validate() đầy đủ bao gồm kiểm tra Payload không được null.
    /// </summary>
    public static object DeserializeAndValidate(string json, Type payloadType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(payloadType);

        var envelopeType = typeof(MessageEnvelope<>).MakeGenericType(payloadType);
        var envelope = JsonSerializer.Deserialize(json, envelopeType, DefaultOptions)
            ?? throw new JsonException("Không thể deserialize MessageEnvelope từ wire JSON.");

        var validateMethod = envelopeType.GetMethod("Validate");
        validateMethod?.Invoke(envelope, null);

        return envelope;
    }

    /// <summary>
    /// Trích xuất nhanh metadata từ wire JSON mà không cần biết trước kiểu Payload (MessageId, MessageType, Version, CorrelationId, OrganizationId, AggregateId).
    /// </summary>
    public static EnvelopeMetadata ExtractMetadata(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var messageId = root.TryGetProperty("messageId", out var midProp) && midProp.TryGetGuid(out var mid)
            ? mid : Guid.Empty;

        var messageType = root.TryGetProperty("messageType", out var mtProp) ? mtProp.GetString() ?? string.Empty : string.Empty;
        var version = root.TryGetProperty("version", out var vProp) && vProp.TryGetInt32(out var v) ? v : 0;
        var correlationId = root.TryGetProperty("correlationId", out var cProp) ? cProp.GetString() ?? string.Empty : string.Empty;

        Guid? causationId = root.TryGetProperty("causationId", out var causProp) &&
            causProp.ValueKind != JsonValueKind.Null &&
            causProp.TryGetGuid(out var caus)
            ? caus : null;

        var aggregateId = root.TryGetProperty("aggregateId", out var aggProp) ? aggProp.GetString() ?? string.Empty : string.Empty;

        Guid? organizationId = root.TryGetProperty("organizationId", out var orgProp) &&
            orgProp.ValueKind != JsonValueKind.Null &&
            orgProp.TryGetGuid(out var org)
            ? org : null;
        var occurredAt = root.TryGetProperty("occurredAt", out var occProp) && occProp.TryGetDateTimeOffset(out var occ)
            ? occ : DateTimeOffset.MinValue;

        var producer = root.TryGetProperty("producer", out var prodProp) ? prodProp.GetString() ?? string.Empty : string.Empty;

        return new EnvelopeMetadata(
            messageId,
            messageType,
            version,
            correlationId,
            causationId,
            aggregateId,
            organizationId,
            occurredAt,
            producer);
    }
}

/// <summary>
/// Metadata cơ bản của envelope phục vụ routing và logging trước khi deserialize payload.
/// </summary>
public sealed record EnvelopeMetadata(
    Guid MessageId,
    string MessageType,
    int Version,
    string CorrelationId,
    Guid? CausationId,
    string AggregateId,
    Guid? OrganizationId,
    DateTimeOffset OccurredAt,
    string Producer)
{
    public bool IsValid =>
        MessageId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(MessageType) &&
        Version > 0 &&
        !string.IsNullOrWhiteSpace(CorrelationId) &&
        !string.IsNullOrWhiteSpace(AggregateId) &&
        OccurredAt.Offset == TimeSpan.Zero &&
        !string.IsNullOrWhiteSpace(Producer);
}
