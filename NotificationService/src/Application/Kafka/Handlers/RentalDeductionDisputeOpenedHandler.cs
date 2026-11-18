using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Common;
using Dprental.NotificationService.Domain.Entities;

namespace Dprental.NotificationService.Application.Kafka.Handlers;

/// <summary>Rental mở tranh chấp khấu trừ cọc: renter phản đối hoặc nhà cung cấp khai thiệt hại vượt cọc.</summary>
public sealed class RentalDeductionDisputeOpenedHandler : IKafkaEventHandler
{
    public string EventType => "RentalDeductionDisputeOpened";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidOrDefault("rentalOrderId");
        var itemId = payload.GetGuidOrDefault("rentalItemId");
        var disputeId = payload.GetGuidOrDefault("disputeId");
        var renterId = payload.GetGuidOrDefault("renterId");
        var supplierId = payload.GetGuidOrDefault("supplierId");
        var version = payload.GetInt64OrDefault("orderVersion");
        if (orderId is null || orderId == Guid.Empty || itemId is null || itemId == Guid.Empty ||
            disputeId is null || disputeId == Guid.Empty || renterId is null || renterId == Guid.Empty ||
            supplierId is null || supplierId == Guid.Empty || version < 1)
            return Result.Failure<NotificationEventIntent>(
                Error.Validation("INVALID_DISPUTE_OPENED_EVENT", "Dispute opened event is missing required identifiers or version."));

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            OrderId: orderId.Value,
            ResourceType: "Dispute",
            ResourceId: disputeId.Value.ToString("D"),
            WatermarkScope: NotificationWatermarkScope.RentalItem,
            WatermarkId: itemId.Value,
            OrderVersion: version,
            IsTerminal: false,
            TerminalState: null,
            RenterId: renterId,
            SupplierId: supplierId,
            ReturnRequestId: null,
            RenderData: new Dictionary<string, object?>
            {
                ["rentalOrderId"] = orderId.Value,
                ["rentalItemId"] = itemId.Value,
                ["depositAmount"] = payload.GetDecimalOrDefault("depositAmount"),
                ["proposedDeductionAmount"] = payload.GetDecimalOrDefault("proposedDeductionAmount"),
                ["deepLinkUrl"] = $"/orders/{orderId.Value}"
            },
            RentalItemId: itemId.Value));
    }
}
