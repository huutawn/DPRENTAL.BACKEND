namespace Dprental.PaymentService.Application.Kafka;

public interface IInvoicePaidKafkaProducer
{
    Task ProduceAsync(
        string topic,
        string key,
        string payload,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken);
}
