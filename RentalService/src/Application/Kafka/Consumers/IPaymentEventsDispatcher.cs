using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Kafka.Consumers;

public interface IPaymentEventsDispatcher
{
    /// <summary>
    /// Intakes a raw Kafka record, validates the envelope, performs canonical deduplication,
    /// and durably persists it to the Inbox table or DLQ.
    /// Returns Failure if persistence fails, so the Kafka offset is NOT committed.
    /// </summary>
    Task<Result<KafkaIntakeDisposition>> ReceiveMessageAsync(
        string messageJson,
        string? topic = null,
        int? partition = null,
        long? offset = null,
        string? publicationProof = null,
        string? messageKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes an Inbox message by re-reading it under an order lock, executing
    /// the domain workflow (InvoicePaid or RefundCompleted), and saving state atomically.
    /// </summary>
    Task<Result> ProcessInboxMessageAsync(
        Guid inboxMessageId,
        CancellationToken cancellationToken = default);

}
