using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Kafka.Producers;

/// <summary>
/// Business producer for staging Inventory commands into the durable Outbox.
/// NOTE: Successful enqueue indicates only that the command was durably staged for Kafka transmission;
/// it DOES NOT indicate that InventoryService has processed or accepted the command.
/// </summary>
public interface IInventoryCommandProducer
{
    Task<Result<ProducerEnqueueResult>> EnqueueApplyReturnedUnitsAsync(
        ApplyReturnedUnitsCommandPayload command,
        CancellationToken cancellationToken = default);
    Task<Result<ProducerEnqueueResult>> EnqueueCreateHoldAsync(
        CreateHoldCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueReleaseAllocationAsync(
        ReleaseAllocationCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);
}
