using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Common;
using Dprental.NotificationService.Domain.Entities;

namespace Dprental.NotificationService.Application.Kafka.Handlers;

/// <summary>
/// Rental đã chốt cọc sau khi Payment xác nhận quyết toán (và đã chuyển phần hoàn nếu có).
/// Đây là thông báo cọc duy nhất cho renter; REFUND_COMPLETED của hoàn cọc bị bỏ để không trùng.
/// </summary>
public sealed class RentalDepositSettlementCompletedHandler : IKafkaEventHandler
{
    public string EventType => "RentalDepositSettlementCompleted";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidOrDefault("rentalOrderId");
        var itemId = payload.GetGuidOrDefault("rentalItemId");
        var renterId = payload.GetGuidOrDefault("renterId");
        var version = payload.GetInt64OrDefault("orderVersion");
        var depositAmount = payload.GetDecimalOrDefault("depositAmount");
        var deductionAmount = payload.GetDecimalOrDefault("finalDeductionAmount");
        var refundAmount = payload.GetDecimalOrDefault("depositRefundAmount");
        if (orderId is null || orderId == Guid.Empty || itemId is null || itemId == Guid.Empty ||
            renterId is null || renterId == Guid.Empty || version < 1 ||
            depositAmount <= 0m || deductionAmount < 0m || refundAmount < 0m)
            return Result.Failure<NotificationEventIntent>(
                Error.Validation("INVALID_DEPOSIT_SETTLEMENT_EVENT", "Deposit settlement event is missing identifiers, version or valid amounts."));

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            OrderId: orderId.Value,
            ResourceType: "RentalItem",
            ResourceId: itemId.Value.ToString("D"),
            WatermarkScope: NotificationWatermarkScope.RentalItem,
            WatermarkId: itemId.Value,
            OrderVersion: version,
            // Rental phát cùng version với RentalItemCompleted; cùng trạng thái kết thúc để không bị coi là xung đột.
            IsTerminal: true,
            TerminalState: "COMPLETED",
            RenterId: renterId,
            SupplierId: null,
            ReturnRequestId: null,
            RenderData: new Dictionary<string, object?>
            {
                ["rentalOrderId"] = orderId.Value,
                ["rentalItemId"] = itemId.Value,
                ["orderCode"] = payload.GetStringOrDefault("orderCode"),
                ["depositAmount"] = depositAmount,
                ["finalDeductionAmount"] = deductionAmount,
                ["depositRefundAmount"] = refundAmount,
                ["deductionReason"] = payload.GetStringOrDefault("deductionReason"),
                ["finalizationSource"] = payload.GetStringOrDefault("finalizationSource"),
                ["deepLinkUrl"] = $"/orders/{orderId.Value}"
            },
            RentalItemId: itemId.Value));
    }
}
