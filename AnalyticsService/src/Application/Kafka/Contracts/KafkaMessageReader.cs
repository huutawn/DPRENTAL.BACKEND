using System.Text.Json;
using Dprental.AnalyticsService.Common;

namespace Dprental.AnalyticsService.Application.Kafka.Contracts;

/// <summary>
/// Đọc message Kafka theo 2 bước: phong bì trước (để biết messageType), payload sau (bằng DTO đúng loại).
/// Lỗi định dạng trả về <see cref="Result"/> thất bại để worker đưa message vào dead letter, không ném exception.
/// </summary>
public static class KafkaMessageReader
{
    public const string SchemaInvalidErrorCode = "ANALYTICS_SCHEMA_INVALID";

    // Web: tên field camelCase, không phân biệt hoa thường; khớp cách Rental và Payment serialize.
    // RespectNullableAnnotations: JSON gửi null vào field không cho phép null (ví dụ string) cũng bị coi là lỗi.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true
    };

    /// <summary>Bước 1: đọc phong bì, giữ nguyên payload dạng JSON.</summary>
    public static Result<KafkaEnvelope> ReadEnvelope(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return Deserialize<KafkaEnvelope>(json, "envelope");
    }

    /// <summary>Bước 2: đọc payload bằng DTO tương ứng với messageType.</summary>
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
            // Chỉ ghi vị trí lỗi (path), không ghi nội dung message để tránh lộ dữ liệu.
            return Error.Validation(SchemaInvalidErrorCode, $"Không đọc được {description} tại '{exception.Path}'.");
        }
    }
}
