using System.Text.Json;
using Dprental.AnalyticsService.Common;

namespace Dprental.AnalyticsService.Application.Events;

/// <summary>
/// Đọc message theo 2 bước: phong bì trước (biết messageType), payload sau (DTO đúng loại).
/// Lỗi định dạng trả <see cref="Result"/> thất bại để message vào dead letter, không ném exception.
/// </summary>
public static class KafkaMessageReader
{
    public const string SchemaInvalidErrorCode = "ANALYTICS_SCHEMA_INVALID";

    // Web: camelCase, không phân biệt hoa thường. RespectNullableAnnotations: null vào field không cho null là lỗi.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true
    };

    public static Result<KafkaEnvelope> ReadEnvelope(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        Result<KafkaEnvelope> envelope = Deserialize<KafkaEnvelope>(json, "envelope");
        if (envelope.IsFailure)
        {
            return envelope;
        }

        if (envelope.Value.MessageId == Guid.Empty || envelope.Value.Version != KafkaEnvelope.SupportedVersion)
        {
            return Error.Validation(SchemaInvalidErrorCode, "Envelope phải có messageId và version = 1.");
        }

        if (envelope.Value.Payload.ValueKind != JsonValueKind.Object)
        {
            return Error.Validation(SchemaInvalidErrorCode, "Payload phải là một JSON object.");
        }

        return envelope;
    }

    public static Result<TPayload> ReadPayload<TPayload>(KafkaEnvelope envelope)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return Deserialize<TPayload>(envelope.Payload.GetRawText(), envelope.MessageType);
    }

    private static Result<T> Deserialize<T>(string json, string description)
        where T : class
    {
        try
        {
            T? value = JsonSerializer.Deserialize<T>(json, Options);
            return value is null
                ? Error.Validation(SchemaInvalidErrorCode, $"Không đọc được {description}: nội dung rỗng.")
                : value;
        }
        catch (JsonException exception)
        {
            // Chỉ ghi vị trí lỗi, không ghi nội dung message
            return Error.Validation(SchemaInvalidErrorCode, $"Không đọc được {description} tại '{exception.Path}'.");
        }
    }
}
