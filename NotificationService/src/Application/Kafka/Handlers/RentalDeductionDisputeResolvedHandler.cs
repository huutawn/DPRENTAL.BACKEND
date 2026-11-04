using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Common;

namespace Dprental.NotificationService.Application.Kafka.Handlers;

public sealed class RentalDeductionDisputeResolvedHandler : IKafkaEventHandler
{
    public string EventType => "RentalDeductionDisputeResolved";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidOrDefault("rentalOrderId");
        var itemId = payload.GetGuidOrDefault("rentalItemId");
        var renterId = payload.GetGuidOrDefault("renterId");
        var supplierId = payload.GetGuidOrDefault("supplierId");
        var decision = payload.GetStringOrDefault("decision");
        var version = payload.GetInt64OrDefault("orderVersion");
        if (orderId is null || orderId == Guid.Empty || itemId is null || itemId == Guid.Empty ||
            renterId is null || renterId == Guid.Empty || supplierId is null || supplierId == Guid.Empty ||
            version < 1 || decision is not ("UpheldInFull" or "Partial" or "Rejected"))
            return Result.Failure<NotificationEventIntent>(
                Error.Validation("INVALID_DISPUTE_RESOLUTION_EVENT", "Dispute resolution event is missing required identifiers, version or decision."));

        return Result.Success(new NotificationEventIntent(
            EventType, orderId.Value, version, false, null, renterId, supplierId, null,
            new Dictionary<string, object?>
            {
                ["rentalOrderId"] = orderId.Value,
                ["rentalItemId"] = itemId.Value,
                ["decision"] = decision,
                ["finalDeductionAmount"] = payload.GetDecimalOrDefault("finalDeductionAmount")
            }));
    }
}
