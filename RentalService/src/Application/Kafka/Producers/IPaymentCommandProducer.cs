using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Kafka.Producers;

/// <summary>
/// Business producer for staging Payment commands into the durable Outbox.
/// NOTE: Successful enqueue indicates only that the command was durably staged for Kafka transmission;
/// it DOES NOT indicate that PaymentService has created or confirmed the invoice.
/// </summary>
public interface IPaymentCommandProducer
{
    Task<Result<ProducerEnqueueResult>> EnqueueCreateInvoiceAsync(
        CreateInvoiceCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);
}
