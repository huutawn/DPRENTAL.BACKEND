namespace Dprental.PaymentService.Application.Kafka;

public interface IEventTransport
{
    Task PublishAsync(string topic, string partitionKey, string payload,
        CancellationToken cancellationToken);
}
