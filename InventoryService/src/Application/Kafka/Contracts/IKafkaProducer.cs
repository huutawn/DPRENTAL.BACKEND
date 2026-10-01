namespace Dprental.InventoryService.Application.Kafka.Contracts;

/// <summary>
/// Abstraction xuất bản thông điệp lên Kafka broker.
/// Không chứa bất kỳ kiểu dữ liệu nào của Confluent.Kafka trong public contract.
/// </summary>
public interface IKafkaProducer
{
    /// <summary>
    /// Xuất bản trực tiếp wire JSON và headers lên Kafka broker với key đã cho.
    /// </summary>
    Task ProduceRawAsync(
        string topic,
        string kafkaKey,
        string payloadJson,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Xuất bản MessageEnvelope được chuẩn hóa lên Kafka broker.
    /// </summary>
    Task ProduceEnvelopeAsync<T>(
        string topic,
        MessageEnvelope<T> envelope,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default);
}
