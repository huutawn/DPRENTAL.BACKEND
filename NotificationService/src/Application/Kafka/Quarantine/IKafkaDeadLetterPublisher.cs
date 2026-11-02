namespace Dprental.NotificationService.Application.Kafka.Quarantine;

public interface IKafkaDeadLetterPublisher
{
    Task PublishToDeadLetterQueueAsync(
        string rawMessage,
        string quarantineId,
        string reason,
        string? errorCode = null,
        CancellationToken cancellationToken = default);
}
