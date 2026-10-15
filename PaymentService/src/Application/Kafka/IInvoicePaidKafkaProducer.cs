namespace Dprental.PaymentService.Application.Kafka;

public interface IInvoicePaidKafkaProducer
{
    Task ProduceAsync(string topic, string key, string payload, CancellationToken cancellationToken);
}
